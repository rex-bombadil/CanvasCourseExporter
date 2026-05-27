using Microsoft.Extensions.Configuration;
using NLog;

namespace CanvasCourseExporter;

class Program
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Mutex name for single-instance enforcement
    private const string MutexName = "Global\\CanvasCourseExporter";

    static async Task<int> Main(string[] args)
    {
        // Prevent overlapping runs (e.g. Task Scheduler starts a second instance)
        using var mutex = new Mutex(false, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Console.Error.WriteLine("Another instance of CanvasCourseExporter is already running. Exiting.");
            return 4;
        }

        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            // --- Canvas API ---
            var baseUrl = (config["Canvas:BaseUrl"]
                ?? throw new InvalidOperationException("Missing Canvas:BaseUrl.")).TrimEnd('/');
            var apiToken = config["Canvas:ApiToken"]
                ?? throw new InvalidOperationException("Missing Canvas:ApiToken.");

            // --- Content export settings ---
            var outputFolder = config["Export:OutputFolder"] ?? "CanvasCourseExport";
            var organizeByTerm = bool.Parse(config["Export:OrganizeByTerm"] ?? "true");
            var unpack = bool.Parse(config["Export:UnpackContent"] ?? "true");
            var keepPackage = bool.Parse(config["Export:KeepOriginalPackage"] ?? "true");
            var pollInterval = int.Parse(config["Export:PollIntervalSeconds"] ?? "5");
            var maxWait = int.Parse(config["Export:MaxWaitMinutes"] ?? "30");
            var maxConcurrent = int.Parse(config["Export:MaxConcurrentExports"] ?? "3");
            var reExportAfterDays = int.Parse(config["Export:ReExportAfterDays"] ?? "7");
            var courseQueryOverride = config["Export:CourseQuery"] ?? "";

            // --- File download settings ---
            var fileDownloadEnabled = bool.Parse(config["FileDownload:Enabled"] ?? "true");

            // --- Connection strings ---
            var oracleConnStr = config.GetConnectionString("Oracle")
                ?? throw new InvalidOperationException("Missing Oracle connection string.");
            var sqlServerConnStr = config.GetConnectionString("SqlServer") ?? "";

            if (fileDownloadEnabled && string.IsNullOrWhiteSpace(sqlServerConnStr))
            {
                Log.Warn("FileDownload is enabled but no SqlServer connection string is configured. " +
                         "File downloads will be skipped.");
                fileDownloadEnabled = false;
            }

            // --- Initialize shared API client (single HttpClient for all Canvas traffic) ---
            CanvasApi.Initialize(baseUrl, apiToken);

            if (!string.IsNullOrWhiteSpace(courseQueryOverride))
            {
                Log.Info("Using config CourseQuery override");
                Log.Debug("Query: {Query}", courseQueryOverride);
            }
            else
            {
                Log.Info("Using default view: canvas_courses_to_export");
            }

            // --- Query Oracle for courses due for export ---
            var tracker = new ExportTracker(oracleConnStr);

            Log.Info("Querying Oracle for courses due for export (default interval: {Days}d)...", reExportAfterDays);
            var courseEntries = await tracker.GetCoursesToExportAsync(reExportAfterDays, courseQueryOverride);
            Log.Info("Found {Count} course(s) to process", courseEntries.Length);

            if (courseEntries.Length == 0)
            {
                Log.Info("All courses are up to date — nothing to export.");
                return 0;
            }

            Directory.CreateDirectory(outputFolder);

            // --- Initialize components ---
            var contentExporter = new ContentExporter();

            AttachmentDownloader? attachmentDownloader = fileDownloadEnabled
                ? new AttachmentDownloader(oracleConnStr, sqlServerConnStr)
                : null;

            Log.Info("Canvas Course Content Exporter starting");
            Log.Info("Courses: {Count} | Concurrent: {Concurrent} | Unpack: {Unpack} | " +
                     "Default interval: {Days}d | File downloads: {FileDownload}",
                courseEntries.Length, maxConcurrent, unpack, reExportAfterDays,
                fileDownloadEnabled ? "enabled" : "disabled");

            // --- Counters ---
            int exported = 0, skipped = 0, failed = 0, timedOut = 0;
            int filesDownloaded = 0, filesFailed = 0;
            long totalFileBytes = 0;
            var countsLock = new object();

            var exportedCourses = new List<string>();
            var failedCourses = new List<string>();
            var timedOutCourses = new List<string>();
            var idMismatches = new List<string>();

            // Cancellation token for fatal errors (disk full, etc.)
            using var cts = new CancellationTokenSource();

            // --- Process courses concurrently ---
            var semaphore = new SemaphoreSlim(maxConcurrent);
            var tasks = courseEntries.Select(async entry =>
            {
                try
                {
                    await semaphore.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    if (cts.IsCancellationRequested) return;

                    // Phase 1: IMSCC content export
                    var outcome = await contentExporter.ExportCourseAsync(
                        entry, outputFolder, organizeByTerm,
                        unpack, keepPackage, pollInterval, maxWait, tracker);

                    lock (countsLock)
                    {
                        switch (outcome.Result)
                        {
                            case ContentExporter.ExportResult.Exported:
                                exported++;
                                exportedCourses.Add(outcome.CourseLabel
                                    ?? $"Canvas {entry.CanvasId} | SIS {entry.SisId}");
                                break;
                            case ContentExporter.ExportResult.Skipped:
                                skipped++;
                                break;
                            case ContentExporter.ExportResult.TimedOut:
                                timedOut++;
                                timedOutCourses.Add(outcome.CourseLabel
                                    ?? $"Canvas {entry.CanvasId} | SIS {entry.SisId}");
                                break;
                            default:
                                failed++;
                                failedCourses.Add(outcome.CourseLabel
                                    ?? $"Canvas {entry.CanvasId} | SIS {entry.SisId}");
                                break;
                        }

                        if (outcome.IdMismatch != null)
                            idMismatches.Add(outcome.IdMismatch);
                    }

                    // Phase 2: File attachment downloads
                    if (attachmentDownloader != null
                        && outcome.CourseDir != null
                        && outcome.Result != ContentExporter.ExportResult.Skipped
                        && !cts.IsCancellationRequested)
                    {
                        try
                        {
                            var dlResult = await attachmentDownloader.DownloadCourseAttachmentsAsync(
                                entry.CanvasId, outcome.CourseDir, cts.Token);

                            lock (countsLock)
                            {
                                filesDownloaded += dlResult.FilesDownloaded;
                                filesFailed += dlResult.FilesFailed;
                                totalFileBytes += dlResult.TotalBytesDownloaded;
                            }
                        }
                        catch (IOException ioEx)
                        {
                            Log.Fatal(ioEx,
                                "FATAL I/O during file download for [{SisId}] Canvas {CanvasId}",
                                entry.SisId ?? "no-sis-id", entry.CanvasId);
                            cts.Cancel();
                        }
                        catch (Exception ex)
                        {
                            Log.Warn(ex,
                                "File download failed for [{SisId}] Canvas {CanvasId} — continuing",
                                entry.SisId ?? "no-sis-id", entry.CanvasId);
                        }
                    }
                }
                catch (IOException ioEx)
                {
                    Log.Fatal(ioEx, "FATAL I/O error on [{SisId}] Canvas {CanvasId} — stopping all exports",
                        entry.SisId ?? "no-sis-id", entry.CanvasId);
                    await tracker.RecordExportAsync(entry.CanvasId, entry.SisId, null,
                        "Failed", DateTime.UtcNow, errorMessage: $"Fatal I/O: {ioEx.Message}");
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to export [{SisId}] Canvas {CanvasId}",
                        entry.SisId ?? "no-sis-id", entry.CanvasId);
                    await tracker.RecordExportAsync(entry.CanvasId, entry.SisId, null,
                        "Failed", DateTime.UtcNow, errorMessage: ex.Message);
                    lock (countsLock)
                    {
                        failed++;
                        failedCourses.Add($"{entry.SisId ?? "no-sis-id"} | Canvas {entry.CanvasId}");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // --- Fatal shutdown check ---
            if (cts.IsCancellationRequested)
            {
                Log.Fatal("Export halted due to fatal I/O error (disk full?). " +
                          "{Exported} successful export(s) recorded. " +
                          "Remaining courses will be picked up on next run.",
                    exported);
                return 3;
            }

            // --- Summary ---
            Log.Info("========== EXPORT SUMMARY ==========");
            Log.Info("Total due: {Total} | Exported: {Exported} | Timed out: {TimedOut} | " +
                     "Failed: {Failed} | Not found: {Skipped}",
                courseEntries.Length, exported, timedOut, failed, skipped);

            if (fileDownloadEnabled)
            {
                Log.Info("Files: {Downloaded} downloaded ({Size}), {Failed} failed",
                    filesDownloaded, FileUtils.FormatSize(totalFileBytes), filesFailed);
            }

            if (exportedCourses.Count > 0)
            {
                Log.Info("--- Exported courses ---");
                foreach (var name in exportedCourses.OrderBy(n => n))
                    Log.Info("  + {CourseName}", name);
            }

            if (timedOutCourses.Count > 0)
            {
                Log.Info("--- Timed out (will retry next run) ---");
                foreach (var name in timedOutCourses.OrderBy(n => n))
                    Log.Info("  ? {CourseName}", name);
            }

            if (failedCourses.Count > 0)
            {
                Log.Info("--- Failed courses ---");
                foreach (var name in failedCourses.OrderBy(n => n))
                    Log.Info("  ! {CourseName}", name);
            }

            if (idMismatches.Count > 0)
            {
                Log.Warn("--- Canvas ID mismatches (update source table) ---");
                foreach (var msg in idMismatches.OrderBy(m => m))
                    Log.Warn("  ~ {Mismatch}", msg);
            }

            if (courseEntries.Length == 0)
                Log.Info("No courses due for export.");
            else if (exported == 0 && failed == 0 && timedOut == 0 && idMismatches.Count == 0)
                Log.Info("No courses were successfully exported this run.");

            Log.Info("====================================");

            if (failed > 0)
                Log.Error("Export run completed with {FailCount} failure(s) out of {Total} course(s)",
                    failed, courseEntries.Length);

            return failed > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception — exporter terminated");
            return 2;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }
}