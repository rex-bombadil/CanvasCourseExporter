# Database Setup

The framework requires two database layers:

1. **Canvas Data 2 (SQL Server)** — read-only, managed by Instructure's CD2 sync process
2. **Operational database** — read/write, managed by you, for export scheduling and tracking

This document covers the operational database setup. CD2 is provisioned through your Canvas Data 2 subscription and loaded by Instructure's tooling.

## Operational Database Objects

### Required: Export Log Table

One row per export attempt. Used for scheduling (when was course X last exported?) and audit trail.

```sql
-- Oracle (Regent's implementation)
CREATE TABLE canvas_export_log (
    id                  NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    course_canvas_id    NUMBER(19) NOT NULL,
    course_sis_id       VARCHAR2(100),
    export_result       VARCHAR2(50) NOT NULL,
    package_url         VARCHAR2(1000),
    file_size_bytes     NUMBER(19),
    started_at          TIMESTAMP WITH TIME ZONE NOT NULL,
    completed_at        TIMESTAMP WITH TIME ZONE,
    error_message       VARCHAR2(4000)
);

CREATE INDEX idx_export_log_course ON canvas_export_log(course_canvas_id, completed_at);

-- Grant access to the service account
GRANT SELECT, INSERT ON canvas_export_log TO your_service_account;
```

### Required: Course Selection View

Returns the set of courses to monitor for export. The framework joins this against the export log to determine which courses are due.

Must return columns: `canvas_id` (Canvas internal ID), `course_id` (SIS ID), `export_interval_days` (optional — NULL uses the config default).

```sql
-- Minimal version: all active courses, weekly
CREATE OR REPLACE VIEW canvas_courses_to_export AS
SELECT
    canvas_id,
    course_id,
    7 AS export_interval_days
FROM your_course_tracking_table
WHERE status = 'active';
```

```sql
-- Regent's version: term-aware with master courses on a different schedule
CREATE OR REPLACE VIEW canvas_courses_to_export AS
SELECT canvas_id, course_id, 7 AS export_interval_days
FROM canvas_course_tracking
WHERE term_code = get_current_term()
UNION ALL
SELECT canvas_id, course_id, 30 AS export_interval_days
FROM canvas_master_courses;
```

### Optional: File Download Log Table

If you enable the file attachment downloader, this table tracks which files have been downloaded to avoid re-downloading on subsequent runs.

```sql
CREATE TABLE canvas_file_download_log (
    id                  NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    course_canvas_id    NUMBER(19) NOT NULL,
    attachment_id       NUMBER(19) NOT NULL,
    source_type         VARCHAR2(50) NOT NULL,  -- 'submission', 'discussion_entry', 'submission_comment'
    file_name           VARCHAR2(500),
    file_size_bytes     NUMBER(19),
    downloaded_at       TIMESTAMP WITH TIME ZONE NOT NULL,
    file_path           VARCHAR2(1000)
);

CREATE INDEX idx_file_dl_course ON canvas_file_download_log(course_canvas_id);
CREATE INDEX idx_file_dl_attach ON canvas_file_download_log(attachment_id);
```

## Verifying Your CD2 Tables

Before running the framework, verify that the required CD2 tables exist and are populated in your SQL Server instance:

```sql
-- Verify key tables have data
SELECT 'courses' AS tbl, COUNT(*) AS rows FROM courses
UNION ALL SELECT 'enrollments', COUNT(*) FROM enrollments
UNION ALL SELECT 'submissions', COUNT(*) FROM submissions
UNION ALL SELECT 'users', COUNT(*) FROM users
UNION ALL SELECT 'assignments', COUNT(*) FROM assignments
UNION ALL SELECT 'discussion_entries', COUNT(*) FROM discussion_entries
UNION ALL SELECT 'submission_comments', COUNT(*) FROM submission_comments;

-- Check if discussion_entries has message content (critical for Phase 2)
SELECT TOP 5 id, LEFT(CAST(message AS VARCHAR(200)), 200) AS message_preview
FROM discussion_entries
WHERE CAST(message AS VARCHAR(MAX)) IS NOT NULL
  AND CAST(message AS VARCHAR(MAX)) <> '';

-- Check submissions body field
SELECT TOP 5 id, LEFT(CAST(body AS VARCHAR(200)), 200) AS body_preview
FROM submissions
WHERE CAST(body AS VARCHAR(MAX)) IS NOT NULL
  AND CAST(body AS VARCHAR(MAX)) <> '';
```

If `discussion_entries.message` is empty/null across all rows, your CD2 sync may not include message content, and you would need to fall back to the Canvas REST API for discussion bodies.
