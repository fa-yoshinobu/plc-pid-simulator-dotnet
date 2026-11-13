## Download
- [Windows x64 ZIP]({{DOWNLOAD_URL}})
- [SHA-256 checksum]({{CHECKSUM_URL}})

## Getting started
Extract the ZIP file and run `PidSimulator.exe`. Keep `DEMO.psim` in the same folder.

Requirements: Windows 10 version 2004 or later (x64). The .NET runtime is included.

## Changes
### Added
- Bounded diagnostic logs, per-target error containment and recovery saves for unsaved projects.
- Build/test CI, headless ViewModel tests and weekly bundled runtime update checks.

### Changed
- Publish the executable, fixed-name distribution ZIP and SHA-256 checksum under `artifacts/release/` in local builds and CI.
- Keep default CSV destinations portable and preserve unknown project fields during editing and saving.
- Separate project, connection, registration, communication recovery and FORCE responsibilities.
- Warn before old events are discarded, and log unexpected communication cleanup failures.
- Update the bundled .NET and Windows Desktop runtimes to 10.0.15.

### Fixed
- Reject incomplete VirusTotal responses without a matching API-provided executable SHA-256.
- Keep event, CSV and trend timestamps aligned after sleep or clock changes, with monotonic PLC freshness checks.
- Cancel pending control starts during error recovery and preserve the current project if new connection settings are invalid.
- Open the actual log and recovery locations when a packaged launcher redirects AppData.

<!-- virustotal:start -->
{{VIRUSTOTAL_CONTENT}}
<!-- virustotal:end -->
