## Download
- [Windows x64 ZIP]({{DOWNLOAD_URL}})
- [SHA-256 checksum]({{CHECKSUM_URL}})

## Getting started
Extract the ZIP file and run `PidSimulator.exe`. Keep `DEMO.psim` in the same folder.

Requirements: Windows 10 version 2004 or later (x64). The .NET runtime is included.

## Changes
### Added
- Add Modbus TCP client support with configurable Unit ID, 32-bit word order and a read-only connection probe.
- Support coils, discrete inputs, input registers and holding registers, with Modbus address suggestions for target registration.

### Changed
- Use AMWD.Protocols.Modbus.Tcp 0.7.0 on .NET 10.
- Integrate Modbus settings, project save/open and target duplication with the current reliability improvements.

<!-- virustotal:start -->
{{VIRUSTOTAL_CONTENT}}
<!-- virustotal:end -->
