## Download
- [Windows x64 ZIP]({{DOWNLOAD_URL}})
- [SHA-256 checksum]({{CHECKSUM_URL}})

## Getting started
Extract the ZIP file and run `PidSimulator.exe`. Keep `DEMO.psim` in the same folder.

Requirements: Windows 10 version 2004 or later (x64). The .NET runtime is included.

## Changes
### Changed
- Publish the executable, fixed-name distribution ZIP and SHA-256 checksum under `artifacts/release/` in local builds and CI.

### Fixed
- Reject incomplete VirusTotal responses without a matching API-provided executable SHA-256.

<!-- virustotal:start -->
{{VIRUSTOTAL_CONTENT}}
<!-- virustotal:end -->
