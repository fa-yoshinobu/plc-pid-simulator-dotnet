# Changelog

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.1.4] - 2026-11-13

### Added

- Add Modbus TCP client support with configurable Unit ID, 32-bit word order and a read-only connection probe.
- Support Modbus register and bit addresses using AMWD.Protocols.Modbus.Tcp 0.7.0.

## [0.1.3] - 2026-11-13

### Added

- Added bounded diagnostic logs, per-target fault handling and separate recovery saves for unsaved projects.
- Added regular build/test CI, headless ViewModel tests and weekly checks for bundled runtime updates.

### Changed

- Aligned local and CI release output at `artifacts/release/`, with a fixed-name distribution ZIP and UTF-8/LF SHA-256 checksum.
- Keep default CSV destinations portable and preserve unknown JSON fields when projects are edited and saved.
- Separate project, PLC connection, registration, communication recovery and FORCE responsibilities.
- Update the bundled .NET and Windows Desktop runtimes to 10.0.15.
- Warn before event retention discards older records and record unexpected communication cleanup failures.

### Fixed

- Reject incomplete VirusTotal responses without a matching API-provided executable SHA-256.
- Keep event, CSV and trend timestamps aligned after sleep or clock changes, using monotonic time for PLC freshness.
- Cancel pending control starts during error recovery and retain the current project when new connection settings are rejected.
- Open the actual log and recovery locations when a packaged launcher redirects AppData.


## [0.1.2] - 2026-10-27

### Changed

- Standardized fixed-name Windows ZIP assets, ZIP SHA-256 checksums and English release notes.

### Fixed

- Unified VirusTotal completion checks and preserved unrelated release notes during rescans.

## [0.1.1] - 2026-09-27

### Added

- KEYENCE PLCのHost Link通信（TCP/UDP）とKV STUDIOシミュレーターに対応。
- MELSECの接続先に、自局・CPU 1～4・制御系／待機系・A系／B系を選択する設定を追加。
- 異常をまとめて解除する「全異常リセット」を追加。
- 詳細画面で制御対象を切り替える「前の対象」「次の対象」を追加。

### Changed

- 「一覧へ戻る」「登録内容編集」「複製」などのボタン表示を改善。

### Fixed

- 停止中に制御対象を切り替えた際、トレンドの表示範囲が更新されない問題を修正。

## [0.1.0] - 2026-09-26

### Added

- 初版。

[Unreleased]: https://github.com/fa-yoshinobu/plc-pid-simulator-dotnet/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/fa-yoshinobu/plc-pid-simulator-dotnet/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/fa-yoshinobu/plc-pid-simulator-dotnet/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/fa-yoshinobu/plc-pid-simulator-dotnet/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/fa-yoshinobu/plc-pid-simulator-dotnet/releases/tag/v0.1.0
