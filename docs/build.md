# ビルド・開発

アプリの操作は [使い方](usage.md)、GitHub Releasesへの公開は [リリース手順](releasing.md) を参照してください。

## 必要な環境

- Windows 10 / 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Git（以下のソース取得コマンドを使う場合）

## ソースの取得

PowerShellで実行します。以降のコマンドも、リポジトリのルートで実行してください。

```powershell
git clone https://github.com/fa-yoshinobu/plc-pid-simulator-dotnet.git
cd plc-pid-simulator-dotnet
```

## 配布用EXEの作成

```powershell
.\build.bat
```

Release構成でテストを実行し、成功したらWindows x64向けのEXEを作成します。出力先は `publish\win-x64-single` で、次の2ファイルを配置します。

```text
publish/win-x64-single/
├─ PidSimulator.exe
└─ DEMO.psim
```

EXEは.NETランタイムを含む単一ファイルです。配布先に.NETのインストールは不要です。`DEMO.psim` はEXEと同じフォルダに置いてください。

起動中の `PidSimulator.exe` がある場合はビルドを中止します。アプリを閉じてから、同じコマンドを再実行してください。出力先は毎回同じフォルダを作り直します。

テストを省略して発行する場合：

```powershell
.\build.bat notest
```

作成したEXEを起動する場合：

```powershell
.\start.bat
```

## 開発中の実行とテスト

```powershell
dotnet build PidSimulator.sln
dotnet run --project src/PidSimulator.App
```

テストのみ実行する場合：

```powershell
dotnet test tests/PidSimulator.Tests -c Release
```

`DEMO.psim` は通常のビルドでも実行ファイルと同じフォルダにコピーされます。

現在の依存関係では、ビルド時に `SkiaSharp.Views.WPF` の互換性に関する `NU1701` 警告が出ます。

## ソースの構成

| 場所 | 内容 |
| --- | --- |
| `src/PidSimulator.Core` | プロセスモデル、演算、ダミーPLC、プロジェクト保存、登録チェック |
| `src/PidSimulator.Plc.Slmp` | MELSEC SLMP通信 |
| `src/PidSimulator.App` | WPF画面とViewModel |
| `tests/PidSimulator.Tests` | モデル・通信・保存などの自動テストとテスト用SLMPサーバ |
| `tools/Artwork` | アプリアイコンとソーシャルプレビュー画像の生成 |
| `docs` | 仕様・計算の説明・画像 |

周期処理、通信ループ、プロジェクト形式などの詳細は [仕様書](specification.md) を参照してください。

## 使用ライブラリとライセンス

| ライブラリ | 用途 | ライセンス |
| --- | --- | --- |
| [PlcComm.Slmp](https://github.com/fa-yoshinobu/plc-comm-slmp-dotnet) | MELSEC SLMP通信 | MIT |
| [ScottPlot.WPF](https://scottplot.net/) | トレンドグラフ | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM | MIT |

使用バージョンは [アプリのプロジェクトファイル](../src/PidSimulator.App/PidSimulator.App.csproj) と [SLMPのプロジェクトファイル](../src/PidSimulator.Plc.Slmp/PidSimulator.Plc.Slmp.csproj) に記載しています。本アプリのライセンスは [MIT License](../LICENSE) です。

## アイコン・ソーシャルプレビュー画像

[tools/Artwork/Program.cs](../tools/Artwork/Program.cs) を編集し、次のコマンドで画像を作り直します。

```powershell
dotnet run --project tools/Artwork
```

アプリアイコン `src/PidSimulator.App/Assets/AppIcon.ico`・`AppIcon.png` と、`docs/images/social-preview.png`（1280×640）を生成します。

GitHubで共有時の画像を更新するときは、リポジトリの **Settings → General → Social preview** から `social-preview.png` をアップロードしてください。
