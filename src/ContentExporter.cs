using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NLog;

namespace CanvasCourseExporter;

/// <summary>
/// Exports a single Canvas course as an IMSCC Common Cartridge package.
/// Handles: Canvas API interaction (start export, poll, download),
/// optional unpacking into organized folders, and SIS ID fallback lookup.
/// </summary>
class ContentExporter
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public enum ExportResult { Exported, Skipped, Failed, TimedOut }

    public record CourseInfo(long Id, string Name, string? Code, string? TermName);

    public record ExportOutcome(
        ExportResult Result,
        string? CourseLabel,
        string? IdMismatch,
        string? CourseDir);

    // ========================================================================
    // EXPORT PIPELINE — single course
    // ========================================================================

    /// <summary>
    /// Run the full IMSCC export pipeline for a single course:
    ///   1. Resolve course info (Canvas ID, fall back to SIS ID)
    ///   2. Trigger content export via Canvas API
    ///   3. Poll until complete
    ///   4. Download .imscc package
    ///   5. Optionally unpack into organized folders
    ///   6. Record result in Oracle
    /// </summary>
    public async Task<ExportOutcome> ExportCourseAsync(
        CourseEntry entry, string outputFolder, bool organizeByTerm,
        bool unpack, bool keepPackage, int pollInterval, int maxWait,
        ExportTracker tracker)
    {
        var courseId = entry.CanvasId;
        var sisId = entry.SisId ?? "no-sis-id";
        var exportStart = DateTime.UtcNow;

        var courseInfo = await GetCourseInfoAsync(courseId);

        // If not found by Canvas ID, try SIS ID lookup
        if (courseInfo == null && !string.IsNullOrWhiteSpace(entry.SisId))
        {
            Log.Debug("[{SisId}] Canvas ID {CanvasId} not found — trying SIS lookup", sisId, courseId);
            courseInfo = await GetCourseInfoBySisIdAsync(entry.SisId);

            if (courseInfo != null)
            {
                var mismatchMsg = $"SIS '{entry.SisId}': old Canvas ID {entry.CanvasId} → new Canvas ID {courseInfo.Id} ({courseInfo.Name})";
                Log.Warn("ID MISMATCH: {Mismatch}", mismatchMsg);

                courseId = courseInfo.Id;

                await tracker.RecordExportAsync(entry.CanvasId, entry.SisId, courseInfo.Name,
                    "Skipped", exportStart, errorMessage: $"ID mismatch — new Canvas ID: {courseInfo.Id}");

                return new ExportOutcome(ExportResult.Skipped,
                    $"{sisId} | Canvas {entry.CanvasId} | {courseInfo.Name}", mismatchMsg, null);
            }
        }

        if (courseInfo == null)
        {
            Log.Warn("[{SisId}] Canvas ID {CanvasId}: not found by Canvas ID or SIS ID — skipping",
                sisId, entry.CanvasId);

            await tracker.RecordExportAsync(entry.CanvasId, entry.SisId, null,
                "Skipped", exportStart, errorMessage: "Course not found by Canvas ID or SIS ID");

            return new ExportOutcome(ExportResult.Skipped, null, null, null);
        }

        var courseLabel = $"{sisId} | Canvas {courseId} | {courseInfo.Name}";

        Log.Info(">>> [{SisId}] Canvas {CanvasId} — {CourseName} ({CourseCode})",
            sisId, courseId, courseInfo.Name, courseInfo.Code);

        // Build destination directory using SIS ID for stable folder names
        var courseFolder = !string.IsNullOrWhiteSpace(entry.SisId)
            ? FileUtils.SanitizeFileName(entry.SisId)
            : $"canvas_{courseId}";

        string courseDir;
        if (organizeByTerm && !string.IsNullOrWhiteSpace(courseInfo.TermName))
            courseDir = Path.Combine(outputFolder, FileUtils.SanitizeFileName(courseInfo.TermName), courseFolder);
        else
            courseDir = Path.Combine(outputFolder, courseFolder);

        Directory.CreateDirectory(courseDir);

        // Trigger the export
        var exportId = await StartExportAsync(courseId);
        if (exportId == null)
        {
            Log.Error("  [{SisId}] Canvas {CanvasId}: failed to start export", sisId, courseId);
            await tracker.RecordExportAsync(courseId, entry.SisId, courseInfo.Name,
                "Failed", exportStart, errorMessage: "Failed to start Canvas export");
            return new ExportOutcome(ExportResult.Failed, courseLabel, null, courseDir);
        }

        Log.Debug("  [{SisId}] Canvas {CanvasId}: export started (ID: {ExportId}), polling...",
            sisId, courseId, exportId);

        // Poll until complete
        var downloadUrl = await PollExportAsync(courseId, exportId.Value, pollInterval, maxWait);
        if (downloadUrl == null)
        {
            Log.Warn("  [{SisId}] Canvas {CanvasId}: export timed out — will retry next run", sisId, courseId);
            await tracker.RecordExportAsync(courseId, entry.SisId, courseInfo.Name,
                "TimedOut", exportStart, errorMessage: "Export timed out waiting for Canvas");
            return new ExportOutcome(ExportResult.TimedOut, courseLabel, null, courseDir);
        }

        // Download the .imscc package
        var packagePath = Path.Combine(courseDir, $"{courseFolder}.imscc");
        await DownloadPackageAsync(downloadUrl, packagePath);

        var fileSize = new FileInfo(packagePath).Length;
        Log.Info("    [{SisId}] Downloaded {Size}", sisId, FileUtils.FormatSize(fileSize));

        // Optionally unpack and organize
        if (unpack)
        {
            Log.Debug("    [{SisId}] Unpacking...", sisId);
            await UnpackCourseContentAsync(packagePath, courseDir, courseInfo);
            Log.Info("    [{SisId}] Content extracted", sisId);
        }

        // Optionally remove the raw package
        if (!keepPackage && File.Exists(packagePath))
            File.Delete(packagePath);

        await tracker.RecordExportAsync(courseId, entry.SisId, courseInfo.Name,
            "Exported", exportStart, fileSizeBytes: fileSize);
        return new ExportOutcome(ExportResult.Exported, courseLabel, null, courseDir);
    }

    // ========================================================================
    // CANVAS API: Course info
    // ========================================================================

    public async Task<CourseInfo?> GetCourseInfoAsync(long courseId)
    {
        using var doc = await CanvasApi.GetAsync($"/api/v1/courses/{courseId}?include[]=term");
        if (doc == null) return null;

        var r = doc.RootElement;

        string? termName = null;
        if (r.TryGetProperty("term", out var t) && t.TryGetProperty("name", out var tn))
            termName = tn.GetString();

        return new CourseInfo(
            r.GetProperty("id").GetInt64(),
            r.GetProperty("name").GetString() ?? $"Course {courseId}",
            r.TryGetProperty("course_code", out var cc) ? cc.GetString() : null,
            termName
        );
    }

    /// <summary>
    /// Look up a course by SIS ID when the Canvas internal ID is stale.
    /// </summary>
    public async Task<CourseInfo?> GetCourseInfoBySisIdAsync(string sisId)
    {
        var encodedSisId = Uri.EscapeDataString(sisId);
        using var doc = await CanvasApi.GetAsync(
            $"/api/v1/courses/sis_course_id:{encodedSisId}?include[]=term");

        if (doc == null) return null;

        var r = doc.RootElement;

        string? termName = null;
        if (r.TryGetProperty("term", out var t) && t.TryGetProperty("name", out var tn))
            termName = tn.GetString();

        return new CourseInfo(
            r.GetProperty("id").GetInt64(),
            r.GetProperty("name").GetString() ?? $"SIS {sisId}",
            r.TryGetProperty("course_code", out var cc2) ? cc2.GetString() : null,
            termName
        );
    }

    // ========================================================================
    // CANVAS API: Start content export
    // ========================================================================

    private async Task<long?> StartExportAsync(long courseId)
    {
        using var doc = await CanvasApi.PostFormAsync(
            $"/api/v1/courses/{courseId}/content_exports",
            new[] { new KeyValuePair<string, string>("export_type", "common_cartridge") });

        if (doc == null) return null;
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    // ========================================================================
    // CANVAS API: Poll export progress
    // ========================================================================

    private async Task<string?> PollExportAsync(long courseId, long exportId, int pollSeconds, int maxMinutes)
    {
        var deadline = DateTime.UtcNow.AddMinutes(maxMinutes);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollSeconds * 1000);

            using var doc = await CanvasApi.GetAsync(
                $"/api/v1/courses/{courseId}/content_exports/{exportId}");

            if (doc == null) continue;

            var root = doc.RootElement;

            var state = root.TryGetProperty("workflow_state", out var ws)
                ? ws.GetString() : "unknown";

            switch (state)
            {
                case "exported":
                    if (root.TryGetProperty("attachment", out var att) &&
                        att.TryGetProperty("url", out var url))
                    {
                        return url.GetString();
                    }
                    Log.Warn("Course {CourseId}: export completed but no download URL found", courseId);
                    return null;

                case "failed":
                    Log.Error("Course {CourseId}: export failed on Canvas side", courseId);
                    return null;

                case "exporting":
                case "created":
                    if (root.TryGetProperty("progress_url", out var pu))
                    {
                        var pct = await GetProgressPercentAsync(pu.GetString()!);
                        if (pct.HasValue)
                            Log.Debug("Course {CourseId}: exporting {Pct:F0}%", courseId, pct.Value);
                    }
                    break;
            }
        }

        return null;
    }

    private async Task<double?> GetProgressPercentAsync(string progressUrl)
    {
        try
        {
            using var doc = await CanvasApi.GetAsync(progressUrl);
            if (doc == null) return null;

            if (doc.RootElement.TryGetProperty("completion", out var c) &&
                c.ValueKind == JsonValueKind.Number)
                return c.GetDouble();
        }
        catch { }

        return null;
    }

    // ========================================================================
    // Download the .imscc package
    // ========================================================================

    private static async Task DownloadPackageAsync(string url, string destPath)
    {
        await CanvasApi.DownloadFileAsync(url, destPath);
    }

    // ========================================================================
    // UNPACK: Extract and organize Common Cartridge content
    // ========================================================================

    private async Task UnpackCourseContentAsync(string packagePath, string courseDir, CourseInfo course)
    {
        var extractDir = Path.Combine(courseDir, "_raw_cartridge");

        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);

        SafeExtractZip(packagePath, extractDir, course.Id);

        var manifestPath = Path.Combine(extractDir, "imsmanifest.xml");
        if (!File.Exists(manifestPath))
        {
            Log.Warn("Course {CourseId}: no imsmanifest.xml found in package", course.Id);
            return;
        }

        var manifest = XDocument.Load(manifestPath);
        var ns = manifest.Root?.GetDefaultNamespace() ?? XNamespace.None;

        await ExtractAssignmentsAsync(extractDir, Path.Combine(courseDir, "Assignments"), course);
        await ExtractDiscussionsAsync(extractDir, Path.Combine(courseDir, "Discussions"), course);
        await ExtractPagesAsync(extractDir, Path.Combine(courseDir, "Pages"), course);
        await ExtractQuizzesAsync(extractDir, Path.Combine(courseDir, "Quizzes"), course);
        await ExtractAnnouncementsAsync(extractDir, Path.Combine(courseDir, "Announcements"), course);
        ExtractModuleStructure(manifest, ns, Path.Combine(courseDir, "Modules"), course);
        CopyWebResources(extractDir, Path.Combine(courseDir, "Files"), course);
        await ExtractSyllabusAsync(extractDir, Path.Combine(courseDir, "Syllabus"), course);

        try { Directory.Delete(extractDir, true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Safe ZIP extraction
    // -----------------------------------------------------------------------

    private void SafeExtractZip(string zipPath, string destDir, long courseId)
    {
        int extracted = 0, skippedDuplicates = 0, skippedErrors = 0;

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var sanitizedPath = FileUtils.SanitizeEntryPath(entry.FullName);
            var destPath = Path.Combine(destDir, sanitizedPath);

            // Guard against zip-slip
            var fullDest = Path.GetFullPath(destPath);
            var fullDir = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);
            if (!fullDest.StartsWith(fullDir))
            {
                Log.Warn("Course {CourseId}: skipping suspicious zip entry '{Entry}'",
                    courseId, entry.FullName);
                skippedErrors++;
                continue;
            }

            try
            {
                var entryDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(entryDir))
                    Directory.CreateDirectory(entryDir);

                if (File.Exists(destPath))
                    skippedDuplicates++;

                entry.ExtractToFile(destPath, overwrite: true);
                extracted++;
            }
            catch (PathTooLongException)
            {
                Log.Warn("Course {CourseId}: path too long, skipping '{Entry}'",
                    courseId, entry.FullName);
                skippedErrors++;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Course {CourseId}: failed to extract '{Entry}'",
                    courseId, entry.FullName);
                skippedErrors++;
            }
        }

        if (skippedDuplicates > 0 || skippedErrors > 0)
        {
            Log.Info("Course {CourseId}: extracted {Extracted} files ({Dupes} duplicate(s) overwritten, {Errors} skipped)",
                courseId, extracted, skippedDuplicates, skippedErrors);
        }
    }

    // -----------------------------------------------------------------------
    // Content extraction methods
    // -----------------------------------------------------------------------

    private async Task ExtractAssignmentsAsync(string extractDir, string outDir, CourseInfo course)
    {
        var htmlFiles = FindContentFiles(extractDir, "*assignment*", "*.html");
        int count = 0;

        foreach (var file in htmlFiles)
        {
            var content = await File.ReadAllTextAsync(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            Directory.CreateDirectory(outDir);
            var name = Path.GetFileNameWithoutExtension(file);
            var safeName = FileUtils.SanitizeFileName(CleanIdentifier(name));
            if (safeName.Length > 80) safeName = safeName[..80];

            await WriteContentPairAsync(outDir, safeName, content, course.Name);
            count++;
        }

        if (count > 0)
            Log.Debug("Course {CourseId}: {Count} assignment(s) extracted", course.Id, count);
    }

    private async Task ExtractDiscussionsAsync(string extractDir, string outDir, CourseInfo course)
    {
        var htmlFiles = FindContentFiles(extractDir, "*discussion*", "*.html")
            .Where(f => !f.Contains("announcement", StringComparison.OrdinalIgnoreCase)).ToList();
        int count = 0;

        foreach (var file in htmlFiles)
        {
            var content = await File.ReadAllTextAsync(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            Directory.CreateDirectory(outDir);
            var name = Path.GetFileNameWithoutExtension(file);
            var safeName = FileUtils.SanitizeFileName(CleanIdentifier(name));
            if (safeName.Length > 80) safeName = safeName[..80];

            await WriteContentPairAsync(outDir, safeName, content, course.Name);
            count++;
        }

        if (count > 0)
            Log.Debug("Course {CourseId}: {Count} discussion(s) extracted", course.Id, count);
    }

    private async Task ExtractPagesAsync(string extractDir, string outDir, CourseInfo course)
    {
        var htmlFiles = FindContentFiles(extractDir, "*wiki*", "*.html");
        htmlFiles.AddRange(FindContentFiles(extractDir, "*pages*", "*.html"));
        htmlFiles = htmlFiles.Distinct().ToList();
        int count = 0;

        foreach (var file in htmlFiles)
        {
            var content = await File.ReadAllTextAsync(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            Directory.CreateDirectory(outDir);
            var name = Path.GetFileNameWithoutExtension(file);
            var safeName = FileUtils.SanitizeFileName(CleanIdentifier(name));
            if (safeName.Length > 80) safeName = safeName[..80];

            await WriteContentPairAsync(outDir, safeName, content, course.Name);
            count++;
        }

        if (count > 0)
            Log.Debug("Course {CourseId}: {Count} page(s) extracted", course.Id, count);
    }

    private async Task ExtractQuizzesAsync(string extractDir, string outDir, CourseInfo course)
    {
        var htmlFiles = FindContentFiles(extractDir, "*quiz*", "*.html");
        int count = 0;

        foreach (var file in htmlFiles)
        {
            var content = await File.ReadAllTextAsync(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            Directory.CreateDirectory(outDir);
            var name = Path.GetFileNameWithoutExtension(file);
            var safeName = FileUtils.SanitizeFileName(CleanIdentifier(name));
            if (safeName.Length > 80) safeName = safeName[..80];

            await WriteContentPairAsync(outDir, safeName, content, course.Name);
            count++;
        }

        if (count > 0)
            Log.Debug("Course {CourseId}: {Count} quiz(zes) extracted", course.Id, count);
    }

    private async Task ExtractAnnouncementsAsync(string extractDir, string outDir, CourseInfo course)
    {
        var htmlFiles = FindContentFiles(extractDir, "*announcement*", "*.html");
        int count = 0;

        foreach (var file in htmlFiles)
        {
            var content = await File.ReadAllTextAsync(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            Directory.CreateDirectory(outDir);
            var name = Path.GetFileNameWithoutExtension(file);
            var safeName = FileUtils.SanitizeFileName(CleanIdentifier(name));
            if (safeName.Length > 80) safeName = safeName[..80];

            await WriteContentPairAsync(outDir, safeName, content, course.Name);
            count++;
        }

        if (count > 0)
            Log.Debug("Course {CourseId}: {Count} announcement(s) extracted", course.Id, count);
    }

    private async Task ExtractSyllabusAsync(string extractDir, string outDir, CourseInfo course)
    {
        var candidates = Directory.GetFiles(extractDir, "syllabus*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).ToList();

        var settingsFile = Path.Combine(extractDir, "course_settings", "course_settings.xml");
        if (File.Exists(settingsFile))
        {
            try
            {
                var xdoc = XDocument.Load(settingsFile);
                var syllabusBody = xdoc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "syllabus_body")?.Value;

                if (!string.IsNullOrWhiteSpace(syllabusBody))
                {
                    Directory.CreateDirectory(outDir);
                    await WriteContentPairAsync(outDir, "Syllabus", syllabusBody, course.Name);
                    Log.Debug("Course {CourseId}: syllabus extracted from course settings", course.Id);
                    return;
                }
            }
            catch { }
        }

        foreach (var file in candidates)
        {
            var content = await File.ReadAllTextAsync(file);
            if (string.IsNullOrWhiteSpace(content)) continue;

            Directory.CreateDirectory(outDir);
            await WriteContentPairAsync(outDir, "Syllabus", content, course.Name);
            Log.Debug("Course {CourseId}: syllabus extracted", course.Id);
            return;
        }
    }

    private void ExtractModuleStructure(XDocument manifest, XNamespace ns, string outDir, CourseInfo course)
    {
        var organizations = manifest.Descendants(ns + "organization").ToList();
        if (organizations.Count == 0) return;

        var lines = new List<string>
        {
            $"Module Structure for: {course.Name}",
            new string('=', 60),
            ""
        };

        foreach (var org in organizations)
        {
            foreach (var item in org.Descendants(ns + "item"))
            {
                var title = item.Element(ns + "title")?.Value ?? "";
                var depth = item.Ancestors(ns + "item").Count();
                var indent = new string(' ', depth * 2);

                if (!string.IsNullOrWhiteSpace(title))
                    lines.Add($"{indent}{title}");
            }
        }

        if (lines.Count > 3)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllLines(Path.Combine(outDir, "Module_Structure.txt"), lines);
            Log.Debug("Course {CourseId}: module structure extracted", course.Id);
        }
    }

    private void CopyWebResources(string extractDir, string outDir, CourseInfo course)
    {
        var webResources = Path.Combine(extractDir, "web_resources");
        if (!Directory.Exists(webResources)) return;

        var files = Directory.GetFiles(webResources, "*", SearchOption.AllDirectories);
        if (files.Length == 0) return;

        int copied = 0, skipped = 0;

        foreach (var file in files)
        {
            try
            {
                var relative = Path.GetRelativePath(webResources, file);
                var destPath = Path.Combine(outDir, relative);
                var destDir = Path.GetDirectoryName(destPath)!;
                Directory.CreateDirectory(destDir);

                File.Copy(file, destPath, overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Course {CourseId}: failed to copy file '{File}'", course.Id, file);
                skipped++;
            }
        }

        Log.Debug("Course {CourseId}: {Copied} file(s) copied from web_resources{Skipped}",
            course.Id, copied, skipped > 0 ? $" ({skipped} failed)" : "");
    }

    // ========================================================================
    // CONTENT HELPERS
    // ========================================================================

    private static async Task WriteContentPairAsync(string dir, string name, string htmlContent, string courseName)
    {
        var resolved = ResolveCartridgePlaceholders(htmlContent);

        var html = HtmlUtils.WrapHtml(resolved, name, courseName);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.html"), html);

        var text = HtmlUtils.StripHtml(resolved);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.txt"), text);
    }

    /// <summary>
    /// Replace IMS-CC and Canvas-specific placeholder variables with relative paths.
    /// </summary>
    private static string ResolveCartridgePlaceholders(string html)
    {
        html = html.Replace("$IMS-CC-FILEBASE$", "../Files");
        html = html.Replace("$CANVAS_COURSE_REFERENCE$", "..");
        html = html.Replace("$CANVAS_OBJECT_REFERENCE$", "..");
        html = html.Replace("$WIKI_REFERENCE$", "../Pages");
        return html;
    }

    private static List<string> FindContentFiles(string root, string dirPattern, string filePattern)
    {
        var results = new List<string>();
        try
        {
            foreach (var dir in Directory.GetDirectories(root, dirPattern, SearchOption.AllDirectories))
                results.AddRange(Directory.GetFiles(dir, filePattern, SearchOption.TopDirectoryOnly));

            if (dirPattern == "*")
                results.AddRange(Directory.GetFiles(root, filePattern, SearchOption.TopDirectoryOnly));
        }
        catch { }
        return results;
    }

    private static string CleanIdentifier(string name)
    {
        name = Regex.Replace(name, @"^i[0-9a-f]{10,}_?", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^[0-9a-f]{32}_?", "", RegexOptions.IgnoreCase);
        name = name.Replace('_', ' ').Replace('-', ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        if (name.Length > 0)
            name = char.ToUpper(name[0]) + name[1..];
        return string.IsNullOrWhiteSpace(name) ? "Untitled" : name;
    }
}