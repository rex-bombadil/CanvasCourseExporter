using NLog;
using Oracle.ManagedDataAccess.Client;

namespace CanvasCourseExporter;

/// <summary>
/// A course to be exported, as returned by the Oracle scheduling view.
/// </summary>
record CourseEntry(long CanvasId, string? SisId);

/// <summary>
/// Oracle-backed export history for IMSCC course content exports.
/// Records every attempt (success, failure, timeout, skip) for scheduling
/// and audit purposes.
/// </summary>
class ExportTracker
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly string _connStr;

    public ExportTracker(string oracleConnStr)
    {
        _connStr = oracleConnStr;
    }

    /// <summary>
    /// Record an export attempt. Writes immediately to Oracle.
    /// </summary>
    public async Task RecordExportAsync(
        long courseCanvasId,
        string? courseSisId,
        string? courseName,
        string exportResult,
        DateTime startedAt,
        long? fileSizeBytes = null,
        string? errorMessage = null)
    {
        const string sql = @"
            INSERT INTO canvas_export_log
                (course_canvas_id, course_sis_id, course_name,
                 export_result, started_at, completed_at,
                 file_size_bytes, error_message)
            VALUES
                (:canvasId, :sisId, :courseName,
                 :result, :startedAt, SYSTIMESTAMP,
                 :fileSize, :errorMsg)";

        try
        {
            await using var conn = new OracleConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            cmd.Parameters.Add(new OracleParameter("canvasId", courseCanvasId));
            cmd.Parameters.Add(new OracleParameter("sisId", (object?)courseSisId ?? DBNull.Value));
            cmd.Parameters.Add(new OracleParameter("courseName", (object?)courseName ?? DBNull.Value));
            cmd.Parameters.Add(new OracleParameter("result", exportResult));
            cmd.Parameters.Add(new OracleParameter("startedAt", startedAt));
            cmd.Parameters.Add(new OracleParameter("fileSize", (object?)fileSizeBytes ?? DBNull.Value));
            cmd.Parameters.Add(new OracleParameter("errorMsg",
                string.IsNullOrWhiteSpace(errorMessage)
                    ? DBNull.Value
                    : (errorMessage.Length > 4000 ? errorMessage[..4000] : errorMessage)));

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Don't let tracking failures kill the export process
            Log.Warn(ex, "Failed to record export for Canvas ID {CanvasId}", courseCanvasId);
        }
    }

    /// <summary>
    /// Query Oracle for courses due for export, respecting per-course intervals.
    /// </summary>
    public async Task<CourseEntry[]> GetCoursesToExportAsync(
        int defaultIntervalDays, string courseQueryOverride)
    {
        var entries = new List<CourseEntry>();

        var courseSource = !string.IsNullOrWhiteSpace(courseQueryOverride)
            ? courseQueryOverride
            : "SELECT canvas_id, course_id, export_interval_days FROM canvas_courses_to_export";

        var sql = $@"
            SELECT src.canvas_id, src.course_id
            FROM ({courseSource}) src
            LEFT JOIN (
                SELECT course_canvas_id, MAX(completed_at) AS last_exported
                FROM canvas_export_log
                WHERE export_result = 'Exported'
                GROUP BY course_canvas_id
            ) el ON el.course_canvas_id = src.canvas_id
            WHERE el.last_exported IS NULL
               OR el.last_exported < SYSTIMESTAMP - NUMTODSINTERVAL(
                    COALESCE(src.export_interval_days, :defaultDays), 'DAY')
            ORDER BY src.course_id";

        await using var conn = new OracleConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        cmd.Parameters.Add(new OracleParameter("defaultDays", defaultIntervalDays));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new CourseEntry(
                Convert.ToInt64(reader["canvas_id"]),
                reader.IsDBNull(reader.GetOrdinal("course_id")) ? null : reader["course_id"].ToString()
            ));
        }

        return entries.ToArray();
    }
}