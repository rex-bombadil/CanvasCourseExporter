# Canvas LMS Course Data Archival Framework

An open-source framework for automated, unattended archival of Canvas LMS course data. Built and tested in production at Regent University. 

## What This Does

Canvas LMS has no built-in bulk archival capability. This framework solves that by orchestrating three data sources every Canvas institution already has access to:

| Data Source | What It Provides |
|---|---|
| **IMSCC Common Cartridge** (via Content Exports API) | Complete course structure — syllabus, assignments, pages, modules, rubrics, quizzes, files. The only format another LMS can natively import. |
| **Canvas Data 2** (bulk tables in SQL Server) | Structured metadata for file attachments across submissions, discussion entries, and comments. Used to discover which files to download. |
| **Canvas REST API** | Binary file downloads — student-uploaded submissions, discussion attachments. The only path to actual file content. |

No single source provides a complete archive. Used together, they do.

## How It Works

The exporter runs on a schedule (typically nightly) and for each course:

1. Checks an operational database (Oracle) for courses due for export based on configurable intervals
2. Triggers an IMSCC Common Cartridge export via the Canvas Content Exports API
3. Polls until the export completes, then downloads the `.imscc` package
4. Optionally unpacks the package into organized, human-readable folders (HTML pages, plain text, module structure)
5. Queries CD2 for all file attachment IDs referenced in submissions, discussion entries, and submission comments
6. Downloads new files via the Canvas API, skipping anything already downloaded on a previous run
7. Records all results in the operational database for scheduling and audit

Concurrency, rate limiting, retry logic, and single-instance enforcement are built in.

## Architecture

```
src/CanvasCourseExporter/
├── Program.cs                  # Orchestration — config, concurrent task loop, summary
├── ContentExporter.cs          # IMSCC pipeline — trigger, poll, download, unpack
├── AttachmentDownloader.cs     # CD2 queries + Canvas API file downloads
├── ExportTracker.cs            # Oracle scheduling and export logging
├── Shared.cs                   # Canvas API client, HTML/file utilities
└── CanvasCourseExporter.csproj # .NET 8 project file
```

## Prerequisites

- **.NET 10 SDK** (cross-platform — Windows, Linux, macOS)
- **Canvas API token** with scopes for content export and file access
- **Canvas Data 2 subscription** with tables loaded into SQL Server
- **Operational database** for export tracking/scheduling — Regent uses Oracle; any RDBMS works (see [Adaptation Guide](docs/adaptation-guide.md))
- **Scheduled task runner** (Windows Task Scheduler, cron, systemd timer)

## Quick Start

1. Clone this repository
2. Copy `config-templates/appsettings.template.json` to `src/CanvasCourseExporter/appsettings.json`
3. Fill in your Canvas base URL, API token, connection strings, and output paths
4. Copy `config-templates/nlog.template.config` to `src/CanvasCourseExporter/nlog.config`
5. Set up the operational database tables — see [Database Setup](docs/database-setup.md)
6. Build and run:

```bash
cd src/CanvasCourseExporter
dotnet build
dotnet run
```

## Adapting for Your Institution

The framework is designed to be institution-agnostic. The Regent-specific pieces are isolated:

**Must configure** (different for every institution):
- Canvas base URL and API token
- Connection strings (CD2 SQL Server, operational database)
- Output file paths
- Course selection logic (which courses to archive and how often)

**May need to change**:
- Operational database provider — Oracle to PostgreSQL, MySQL, SQL Server, or SQLite. The C# code uses standard ADO.NET interfaces; see [Adaptation Guide](docs/adaptation-guide.md)
- Course selection view — Regent's encodes term structure and master course conventions. Yours could be as simple as "all courses from the current and previous two terms"
- SMTP configuration for error notifications

**Should not need to change**:
- Canvas API interaction (Content Exports, Files API, pagination, rate limiting)
- IMSCC unpacking and content organization
- CD2 query patterns (table schemas are consistent across institutions, though column availability varies)

See [docs/adaptation-guide.md](docs/adaptation-guide.md) for detailed instructions.

## CD2 Schema Notes

Canvas Data 2 tables at Regent in SQL Server have universal quirks the code handles:

- **All columns are `text` type** — numerics, booleans, datetimes all stored as strings
- **Booleans are `'true'`/`'false'` strings** — not bit, not 0/1
- **`attachment_ids` fields are comma-separated** — requires `STRING_SPLIT` handling
- **Some tables may not be in your sync** — check your CD2 configuration

## What Gets Archived Per Course

| Artifact | Description |
|---|---|
| `.imscc` package | Complete course structure — importable into any IMS Common Cartridge-compatible LMS |
| Unpacked content folders | HTML and plain text versions of assignments, discussions, pages, quizzes, announcements, syllabus, module structure |
| `files/` directory | Student-uploaded file attachments from submissions, discussion entries, and comments — named `{attachmentId}_{filename}` for CD2 traceability |

## Project Status

| Component | Status |
|---|---|
| IMSCC Content Export Pipeline | Production |
| Content Unpacking (HTML/TXT/module structure) | Production |
| File Attachment Downloader (submissions, discussions, comments) | Production |
| Oracle Scheduling and Tracking | Production |
| Quiz Attempt Archival (individual student answers) | Designed, not built |
| Course Restoration (round-trip import test) | Not yet tested |

## Known Limitations

- **Restoration not yet validated end-to-end.** IMSCC packages should import into any compliant LMS, but full round-trip with student data has not been tested.
- **LTI tool content not captured.** Third-party integrations (Turnitin, publisher tools) store content externally and only leave launch URLs in Canvas.
- **Canvas API rate limits** constrain file download throughput (~600 requests per 10 minutes per token).

## Contributing

Contributions from other institutions are welcome. Particularly valuable:

- Adaptation guides for non-Oracle operational databases (PostgreSQL, MySQL, SQLite)
- CD2 schema differences specific to your institution
- Canvas API edge cases you encounter
- Restoration testing results

## License

MIT License — see [LICENSE](LICENSE)
