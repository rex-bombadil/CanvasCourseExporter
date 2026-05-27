using System.Text.Json;
using NLog;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Data.SqlClient;

namespace CanvasCourseExporter;

/// <summary>
/// Downloads student-generated file attachments for a course by:
///   1. Querying CD2 (SQL Server) for attachment IDs from submissions,
///      discussion entries, and submission comments
///   2. Checking Oracle for previously downloaded files
///   3. Downloading new files via Canvas API (GET /files/{id} → download URL → binary)
///   4. Recording each download in Oracle for incremental runs
/// </summary>
class AttachmentDownloader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _oracleConnStr;
    private readonly string _sqlServerConnStr;

    public AttachmentDownloader(string oracleConnStr, string sqlServerConnStr)
    {
        _oracleConnStr = oracleConnStr;
        _sqlServerConnStr = sqlServerConnStr;
    }

    /// <summary>
    /// Result of a file download run for a single course.
    /// </summary>
    public record DownloadResult(
        int FilesDownloaded,
        int FilesSkipped,
        int FilesFailed,
        long TotalBytesDownloaded);

    /// <summary>
    /// Download all student-generated file attachments for a course.
    /// Files are saved to {courseDir}/files/{attachmentId}_{filename}.
    /// Only downloads files not already tracked in Oracle.
    /// </summary>
    public async Task<DownloadResult> DownloadCourseAttachmentsAsync(
        long canvasId, string courseDir, CancellationToken ct = default)
    {
        var filesDir = Path.Combine(courseDir, "files");

        // Step 1: Get all attachment IDs from CD2 for this course
        var attachmentIds = await GetAttachmentIdsFromCd2Async(canvasId);

        if (attachmentIds.Count == 0)
        {
            Log.Debug("Course {CourseId}: no file attachments found in CD2", canvasId);
            return new DownloadResult(0, 0, 0, 0);
        }

        Log.Info("Course {CourseId}: {Count} unique attachment(s) found in CD2",
            canvasId, attachmentIds.Count);

        // Step 2: Filter out already-downloaded files
        var alreadyDownloaded = await GetDownloadedAttachmentIdsAsync(canvasId);
        var toDownload = attachmentIds.Except(alreadyDownloaded).ToList();

        int skipped = attachmentIds.Count - toDownload.Count;
        if (skipped > 0)
            Log.Debug("Course {CourseId}: {Skipped} file(s) already downloaded, {Remaining} new",
                canvasId, skipped, toDownload.Count);

        if (toDownload.Count == 0)
            return new DownloadResult(0, skipped, 0, 0);

        // Step 3: Get file metadata from CD2 for logging/naming
        var attachmentMeta = await GetAttachmentMetadataAsync(toDownload);

        // Step 4: Download each file
        Directory.CreateDirectory(filesDir);
        int downloaded = 0, failed = 0;
        long totalBytes = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var attachmentId in toDownload)
        {
            if (ct.IsCancellationRequested) break;

            var meta = attachmentMeta.GetValueOrDefault(attachmentId);
            var displayName = meta?.DisplayName ?? meta?.Filename ?? $"file_{attachmentId}";
            var contentType = meta?.ContentType ?? "unknown";
            int current = downloaded + failed + 1;

            Log.Debug("  Course {CourseId}: [{Current}/{Total}] downloading {Name} ({ContentType})",
                canvasId, current, toDownload.Count, displayName, contentType);

            try
            {
                var result = await DownloadSingleFileAsync(attachmentId, displayName, filesDir);

                if (result != null)
                {
                    await RecordDownloadAsync(canvasId, attachmentId, displayName,
                        result.Size, null);
                    downloaded++;
                    totalBytes += result.Size;

                    Log.Debug("  Course {CourseId}: [{Current}/{Total}] saved {Name} ({Size})",
                        canvasId, current, toDownload.Count, displayName,
                        FileUtils.FormatSize(result.Size));
                }
                else
                {
                    await RecordDownloadAsync(canvasId, attachmentId, displayName,
                        0, "No download URL returned — file may be deleted");
                    failed++;
                    Log.Warn("  Course {CourseId}: [{Current}/{Total}] {Name} — no download URL (deleted?)",
                        canvasId, current, toDownload.Count, displayName);
                }

                // Progress update every 10 files
                if ((downloaded + failed) % 10 == 0)
                    Log.Info("  Course {CourseId}: file download progress — {Current}/{Total} processed " +
                             "({Downloaded} ok, {Failed} failed, {Size} so far)",
                        canvasId, current, toDownload.Count, downloaded, failed,
                        FileUtils.FormatSize(totalBytes));

                await CanvasApi.DelayAsync();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "  Course {CourseId}: [{Current}/{Total}] FAILED {Name} — {Error}",
                    canvasId, current, toDownload.Count, displayName, ex.Message);
                await RecordDownloadAsync(canvasId, attachmentId, displayName,
                    0, ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message);
                failed++;
            }
        }

        sw.Stop();
        Log.Info("Course {CourseId}: file download complete — {Downloaded} downloaded ({Size}), " +
                 "{Failed} failed, {Skipped} previously downloaded, elapsed {Elapsed}",
            canvasId, downloaded, FileUtils.FormatSize(totalBytes),
            failed, skipped, sw.Elapsed.ToString(@"mm\:ss"));

        return new DownloadResult(downloaded, skipped, failed, totalBytes);
    }

    // ========================================================================
    // CD2 QUERIES (SQL Server)
    // ========================================================================

    /// <summary>
    /// Collect all unique attachment IDs from submissions, discussion entries,
    /// and submission comments for a given course.
    /// </summary>
    private async Task<List<long>> GetAttachmentIdsFromCd2Async(long canvasId)
    {
        var ids = new HashSet<long>();

        // CD2 stores everything as text — all comparisons need CAST
        var sql = @"
            -- Submission single attachment
            SELECT CAST(s.attachment_id AS VARCHAR(255)) AS att_id
            FROM submissions s
            WHERE CAST(s.course_id AS VARCHAR(255)) = @courseId
              AND s.attachment_id IS NOT NULL
              AND CAST(s.attachment_id AS VARCHAR(255)) <> ''
              AND CAST(s.workflow_state AS VARCHAR(50)) <> 'deleted'

            UNION

            -- Submission multiple attachments (comma-separated)
            SELECT TRIM(value) AS att_id
            FROM submissions s
            CROSS APPLY STRING_SPLIT(CAST(s.attachment_ids AS VARCHAR(MAX)), ',')
            WHERE CAST(s.course_id AS VARCHAR(255)) = @courseId
              AND s.attachment_ids IS NOT NULL
              AND CAST(s.attachment_ids AS VARCHAR(MAX)) <> ''
              AND CAST(s.workflow_state AS VARCHAR(50)) <> 'deleted'
              AND TRIM(value) <> ''

            UNION

            -- Discussion topic attachments (topic-level, e.g. instructor file on the prompt)
            SELECT CAST(dt.attachment_id AS VARCHAR(255)) AS att_id
            FROM discussion_topics dt
            WHERE CAST(dt.context_type AS VARCHAR(255)) = 'Course'
              AND CAST(dt.context_id AS VARCHAR(255)) = @courseId
              AND dt.attachment_id IS NOT NULL
              AND CAST(dt.attachment_id AS VARCHAR(255)) <> ''
              AND CAST(dt.workflow_state AS VARCHAR(50)) <> 'deleted'

            UNION

            -- Discussion entry attachments (reply-level)
            SELECT CAST(de.attachment_id AS VARCHAR(255)) AS att_id
            FROM discussion_entries de
            INNER JOIN discussion_topics dt
              ON CAST(dt.id AS VARCHAR(255)) = CAST(de.discussion_topic_id AS VARCHAR(255))
            WHERE CAST(dt.context_type AS VARCHAR(255)) = 'Course'
              AND CAST(dt.context_id AS VARCHAR(255)) = @courseId
              AND de.attachment_id IS NOT NULL
              AND CAST(de.attachment_id AS VARCHAR(255)) <> ''
              AND CAST(de.workflow_state AS VARCHAR(50)) <> 'deleted'

            UNION

            -- Submission comment attachments (comma-separated)
            SELECT TRIM(value) AS att_id
            FROM submission_comments sc
            CROSS APPLY STRING_SPLIT(CAST(sc.attachment_ids AS VARCHAR(MAX)), ',')
            WHERE CAST(sc.context_type AS VARCHAR(255)) = 'Course'
              AND CAST(sc.context_id AS VARCHAR(255)) = @courseId
              AND sc.attachment_ids IS NOT NULL
              AND CAST(sc.attachment_ids AS VARCHAR(MAX)) <> ''
              AND TRIM(value) <> ''";

        await using var conn = new SqlConnection(_sqlServerConnStr);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@courseId", canvasId.ToString());
        cmd.CommandTimeout = 120; // CD2 text columns can make these slow

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var val = reader["att_id"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(val) && long.TryParse(val, out var id))
                ids.Add(id);
        }

        return ids.ToList();
    }

    /// <summary>
    /// Get display_name and filename from CD2 attachments for logging/naming.
    /// </summary>
    private async Task<Dictionary<long, AttachmentMeta>> GetAttachmentMetadataAsync(List<long> attachmentIds)
    {
        var result = new Dictionary<long, AttachmentMeta>();
        if (attachmentIds.Count == 0) return result;

        // Batch into groups to avoid massive IN clauses
        foreach (var batch in attachmentIds.Chunk(500))
        {
            var idList = string.Join(",", batch.Select((_, i) => $"@id{i}"));
            var sql = $@"
                SELECT CAST(id AS VARCHAR(255)) AS id,
                       CAST(display_name AS VARCHAR(500)) AS display_name,
                       CAST(filename AS VARCHAR(500)) AS filename,
                       CAST(content_type AS VARCHAR(255)) AS content_type,
                       CAST(size AS VARCHAR(50)) AS size
                FROM attachments
                WHERE CAST(id AS VARCHAR(255)) IN ({idList})";

            await using var conn = new SqlConnection(_sqlServerConnStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);

            for (int i = 0; i < batch.Length; i++)
                cmd.Parameters.AddWithValue($"@id{i}", batch[i].ToString());

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (long.TryParse(reader["id"]?.ToString(), out var id))
                {
                    long.TryParse(reader["size"]?.ToString(), out var size);
                    result[id] = new AttachmentMeta(
                        reader["display_name"]?.ToString(),
                        reader["filename"]?.ToString(),
                        reader["content_type"]?.ToString(),
                        size);
                }
            }
        }

        return result;
    }

    private record AttachmentMeta(string? DisplayName, string? Filename, string? ContentType, long Size);

    // ========================================================================
    // CANVAS API: File download
    // ========================================================================

    private record FileDownloadResult(long Size);

    /// <summary>
    /// Download a single file: GET /api/v1/files/{id} for the download URL,
    /// then stream the binary to disk.
    /// </summary>
    private async Task<FileDownloadResult?> DownloadSingleFileAsync(
        long attachmentId, string displayName, string filesDir)
    {
        // Get file metadata and download URL from Canvas API
        using var doc = await CanvasApi.GetAsync($"/api/v1/files/{attachmentId}");
        if (doc == null)
        {
            Log.Debug("Attachment {Id}: API returned null (deleted or inaccessible)", attachmentId);
            return null;
        }

        var root = doc.RootElement;

        string? downloadUrl = null;
        if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
            downloadUrl = urlProp.GetString();

        if (string.IsNullOrWhiteSpace(downloadUrl))
            return null;

        // Use API display_name if available (more reliable than CD2 for current name)
        if (root.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
        {
            var apiName = dn.GetString();
            if (!string.IsNullOrWhiteSpace(apiName))
                displayName = apiName;
        }

        var safeFileName = $"{attachmentId}_{FileUtils.SanitizeFileName(displayName)}";
        var destPath = Path.Combine(filesDir, safeFileName);

        // Handle potential filename collisions (shouldn't happen with ID prefix, but defensive)
        destPath = FileUtils.GetUniqueFilePath(destPath);

        await CanvasApi.DownloadFileAsync(downloadUrl, destPath);

        var fileSize = new FileInfo(destPath).Length;
        return new FileDownloadResult(fileSize);
    }

    // ========================================================================
    // ORACLE: Download tracking
    // ========================================================================

    /// <summary>
    /// Get attachment IDs already downloaded for a course.
    /// </summary>
    private async Task<HashSet<long>> GetDownloadedAttachmentIdsAsync(long canvasId)
    {
        var ids = new HashSet<long>();

        const string sql = @"
            SELECT attachment_id
            FROM canvas_file_download_log
            WHERE course_canvas_id = :canvasId
              AND download_result = 'Downloaded'";

        try
        {
            await using var conn = new OracleConnection(_oracleConnStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new OracleParameter("canvasId", canvasId));

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                    ids.Add(Convert.ToInt64(reader["attachment_id"]));
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not read download log for course {CourseId} — will re-download all", canvasId);
        }

        return ids;
    }

    /// <summary>
    /// Record a file download attempt in Oracle.
    /// </summary>
    private async Task RecordDownloadAsync(
        long courseCanvasId, long attachmentId, string? displayName,
        long fileSize, string? errorMessage)
    {
        var result = string.IsNullOrEmpty(errorMessage) ? "Downloaded" : "Failed";

        const string sql = @"
            INSERT INTO canvas_file_download_log
                (course_canvas_id, attachment_id, display_name,
                 download_result, downloaded_at, file_size_bytes, error_message)
            VALUES
                (:canvasId, :attachmentId, :displayName,
                 :result, SYSTIMESTAMP, :fileSize, :errorMsg)";

        try
        {
            await using var conn = new OracleConnection(_oracleConnStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            cmd.Parameters.Add(new OracleParameter("canvasId", courseCanvasId));
            cmd.Parameters.Add(new OracleParameter("attachmentId", attachmentId));
            cmd.Parameters.Add(new OracleParameter("displayName",
                (object?)displayName?[..Math.Min(displayName.Length, 500)] ?? DBNull.Value));
            cmd.Parameters.Add(new OracleParameter("result", result));
            cmd.Parameters.Add(new OracleParameter("fileSize", fileSize));
            cmd.Parameters.Add(new OracleParameter("errorMsg",
                string.IsNullOrWhiteSpace(errorMessage) ? DBNull.Value : errorMessage));

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Failed to record download for attachment {AttachmentId}", attachmentId);
        }
    }
}