# Changelog

## [Unreleased]

### Planned
- Quiz attempt extraction (individual student answers per attempt)
- Course restoration testing (IMSCC round-trip import)

## [2.0.0] — 2026-11
- Updated .Net to v10

## [1.0.0] — 2026-05

### Course Content Exporter
- IMSCC Common Cartridge export via Canvas Content Exports API
- Configurable per-course export scheduling (weekly, monthly, custom intervals)
- Concurrent multi-course processing with configurable parallelism
- Adaptive rate limiting with Canvas API backoff and retry logic
- Optional unpack into organized HTML/TXT folders (assignments, discussions, pages, quizzes, announcements, syllabus, module structure)
- SIS ID fallback lookup for courses where Canvas ID has changed
- Global mutex for single-instance enforcement

### File Attachment Downloader
- CD2-driven discovery of all file attachment IDs across submissions, discussion entries, discussion topics, and submission comments
- `STRING_SPLIT` handling for comma-separated `attachment_ids` fields
- Incremental downloads — skips files already tracked in Oracle
- Per-file logging at Debug level; progress summaries every 10 files at Info level
- Files named `{attachmentId}_{displayName}` for direct CD2 traceability

### Infrastructure
- Single shared HttpClient for all Canvas API traffic (unified rate limit tracking)
- Oracle operational tracking (export log + file download log)
- NLog structured logging: console, rolling file, debug file, buffered email on error
- `HttpCompletionOption.ResponseHeadersRead` for large file streaming
- Oracle command timeouts on all queries
- Zip-slip protection during IMSCC extraction
- Path sanitization for Windows filesystem compatibility
