# Adaptation Guide

This document walks through what you need to change to run this framework at your institution.

## Step 1: Canvas API Token

You need an API token with the following scopes:
- Content Exports (to trigger and download IMSCC packages)
- Files (to download student submission attachments)
- Courses (to read course metadata for labeling)

Generate a token at `https://YOUR_INSTANCE.instructure.com/profile/settings` under "Approved Integrations." For production use, create a dedicated service account rather than using a personal token.

## Step 2: Canvas Data 2

You need a CD2 subscription with tables loaded into SQL Server. The framework queries these tables:

**Required for Gradebook Exporter:**
- `courses`
- `enrollments`
- `users`
- `assignments`
- `assignment_groups`
- `submissions`
- `scores`
- `course_sections`

**Required for File Attachment Downloader:**
- `submissions` (has `attachment_id` and `attachment_ids` fields)
- `discussion_entries` (has `attachment_id` field)
- `submission_comments` (has `attachment_ids` field — comma-separated)
- `discussion_topics` (for joining discussions to courses via `context_type`/`context_id`)

## Step 3: Operational Database

The framework uses two database objects for scheduling and tracking:

### Export Log Table

Stores one row per export attempt. Minimum schema:

```sql
CREATE TABLE canvas_export_log (
    id                  INTEGER PRIMARY KEY,  -- auto-increment/sequence
    course_canvas_id    BIGINT NOT NULL,
    course_sis_id       VARCHAR(100),
    export_result       VARCHAR(50) NOT NULL,  -- 'Exported', 'Failed', 'TimedOut', 'Skipped'
    package_url         VARCHAR(1000),
    file_size_bytes     BIGINT,
    started_at          TIMESTAMP NOT NULL,
    completed_at        TIMESTAMP,
    error_message       VARCHAR(4000)
);
```

### Course Selection View

Returns courses eligible for export. Must provide `canvas_id`, `course_id` (SIS ID), and optionally `export_interval_days`:

```sql
-- Simplest possible version: all courses from current and previous two terms
CREATE VIEW canvas_courses_to_export AS
SELECT
    CAST(c.id AS BIGINT) AS canvas_id,
    CAST(c.sis_source_id AS VARCHAR(100)) AS course_id,
    7 AS export_interval_days
FROM canvas_courses c
JOIN canvas_enrollment_terms t ON c.enrollment_term_id = t.id
WHERE t.workflow_state = 'active'
  AND t.end_date > DATEADD(MONTH, -6, GETDATE());
```

The C# code joins this view against the export log to determine which courses are due. You can make the selection logic as simple or complex as your institution needs.

### PostgreSQL Example

```sql
CREATE TABLE canvas_export_log (
    id                  SERIAL PRIMARY KEY,
    course_canvas_id    BIGINT NOT NULL,
    course_sis_id       VARCHAR(100),
    export_result       VARCHAR(50) NOT NULL,
    package_url         VARCHAR(1000),
    file_size_bytes     BIGINT,
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at        TIMESTAMPTZ,
    error_message       TEXT
);

CREATE INDEX idx_export_log_course ON canvas_export_log(course_canvas_id, completed_at);
```

### SQLite (For Testing or Small Institutions)

```sql
CREATE TABLE canvas_export_log (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    course_canvas_id    INTEGER NOT NULL,
    course_sis_id       TEXT,
    export_result       TEXT NOT NULL,
    package_url         TEXT,
    file_size_bytes     INTEGER,
    started_at          TEXT NOT NULL,
    completed_at        TEXT,
    error_message       TEXT
);
```

To use a different database provider, change the connection string in `appsettings.json` and replace `Oracle.ManagedDataAccess.Client` references in `ExportTracker.cs` with your provider's ADO.NET client (e.g., `Npgsql` for PostgreSQL, `Microsoft.Data.SqlClient` for SQL Server, `Microsoft.Data.Sqlite` for SQLite).

## Step 4: Configuration

Copy `config-templates/appsettings.template.json` to `src/CourseContentExporter/appsettings.json` and fill in all values. The template has comments explaining each setting.

## Step 5: File Storage

The `OutputFolder` setting controls where archived courses are stored. Regent uses a Windows UNC path (`\\server\share\CanvasArchive$\`). You can use:
- A local directory
- A network share (UNC or NFS mount)
- A cloud storage mount (if your OS mounts S3/Azure Blob/GCS as a filesystem)

Plan for storage growth. A single course with active students can produce 1–10 GB of submission files. Multiply by your active course count.

## Step 6: Scheduling

Set up a recurring task to run the exporter. The built-in mutex prevents overlapping runs, so it's safe to schedule aggressively.

**Windows Task Scheduler:**
- Program: `dotnet`
- Arguments: `run --project C:\path\to\CourseContentExporter`
- Trigger: Daily at 2:00 AM (or whenever your maintenance window is)

**Linux cron:**
```
0 2 * * * cd /path/to/CourseContentExporter && dotnet run >> /var/log/canvas-export.log 2>&1
```

## Step 7: Monitoring

The system logs via NLog and emails on errors if SMTP is configured. However, a job that doesn't run at all sends no email. Set up external monitoring:

- Query the export log table: `SELECT MAX(completed_at) FROM canvas_export_log WHERE export_result = 'Exported'`. Alert if this is more than 48 hours old.
- Or check file modification times on the output directory.
