# PID Process Simulator 改善ゴール

[README](README.md) · [使い方](docs/usage.md) · [仕様](docs/specification.md) · [ビルド・開発](docs/build.md) · [リリース手順](docs/releasing.md)

v0.1.2（.NET 10 へ移行した時点）のコードレビューに基づく改善項目です。1ゴール＝1つの独立した作業単位で、上から順に着手することを想定しています。

## 共通ルール

- 1ゴールにつき1コミット（または1 PR）。ゴールをまたぐ変更を混ぜない。
- モデルの計算結果（同じ入力に対するPVの推移）を変えない。変える場合はゴールに明記されたものだけ。
- 既存の `*.psim`（形式2）と `DEMO.psim` をそのまま読めること。
- 実機のPLCへ書き込む対象（登録したPVアドレス）を広げない。
- 各ゴールの完了時に、次の共通検証がすべて成功すること。

```powershell
dotnet build PidSimulator.sln -c Release
dotnet test tests/PidSimulator.Tests -c Release
python -m unittest discover -s tests/release
```

## 進捗

| # | ゴール | 優先度 | 状態 |
| --- | --- | --- | --- |
| G1 | 予期しないエラーで試験と未保存の変更を失わない | 高 | 完了 |
| G2 | push/PRでビルドとテストが走る | 高 | 完了 |
| G3 | プロジェクトファイルを別のPC・別の版で開ける | 中 | 完了（別利用者は模擬検証で了承） |
| G4 | PCのスリープや時計の変更で時刻と通信判定が狂わない | 中 | 完了 |
| G5 | 画面側（ViewModel）にテストがあり、大きなクラスが分かれている | 中 | 完了 |
| G6 | 依存関係とリリース用ファイルを整理する | 中 | 完了 |
| G7 | 細かな整理 | 低 | 完了 |

---

## G1: 予期しないエラーで試験と未保存の変更を失わない

**Goal**: 演算・画面・通信のどこで予期しない例外が起きても、アプリが黙って消えない。原因が記録され、未保存のプロジェクトを保存する機会がある。

**背景**: `src/PidSimulator.App/App.xaml.cs` に未処理例外のハンドラーがない。演算スレッド（`SimulationEngine.Loop`）にも `try` がなく、`ControlTarget.Cycle` やモデルの計算で例外が出ると、背景スレッドの未処理例外としてプロセスごと終了する。長時間の試験中に起きると、トレンドと未保存の登録内容がすべて失われ、原因も残らない。演算結果の NaN・無限大は対象ごとに異常として扱っているが、例外は扱っていない。

**対象**: `src/PidSimulator.App/App.xaml.cs`、`src/PidSimulator.Core/SimulationEngine.cs`、`src/PidSimulator.Core/ControlTarget.cs`、`src/PidSimulator.App/ViewModels/MainViewModel.cs`、`docs/usage.md`

**完了条件**
- [x] 演算スレッドで、1つの制御対象の例外をその対象だけの異常（停止・アラーム・イベントログ）として扱い、ほかの対象の演算は続ける。
- [x] 演算スレッド自体が止まった場合は、画面に表示する（止まったことに気付ける）。
- [x] `DispatcherUnhandledException`・`AppDomain.UnhandledException`・`TaskScheduler.UnobservedTaskException` を処理し、内容を記録してメッセージを表示する。
- [x] 画面スレッドの例外では、可能なら動作を続ける。続けられない場合は、未保存の変更があれば別名（例: `元の名前.recovered.psim`）で保存を試みてから終了する。
- [x] ログを `%LOCALAPPDATA%\PidSimulator\logs\` に保存し、サイズ上限つきで古い分を捨てる。記録するのは例外、通信の接続・切断・異常、プロジェクトの読込み・保存の失敗。
- [x] 「バージョン情報」または設定から、ログのフォルダーを開ける。
- [x] モデルが例外を投げても、ほかの対象が動き続けることのテストがある。

**検証**: テスト用に例外を投げるモデルを1つ登録し、その対象だけが異常になり、ほかの対象のトレンドが続くことを確認する。

**対象外**: トレンドの自動保存、クラッシュダンプの採取。

---

## G2: push/PRでビルドとテストが走る

**Goal**: main へのpushとPull Requestのたびに、ビルドと全テストが自動で実行され、失敗が分かる。

**背景**: アプリのテスト（178件）が走るのはタグpush時（`release.yml`）だけ。`release-tools.yml` はリリース用のPythonスクリプトだけを対象にしている。

**対象**: `.github/workflows/`（新規 `ci.yml`）

**完了条件**
- [x] `ci.yml` が `push`（main）と `pull_request` で起動し、`windows-latest` で `dotnet build PidSimulator.sln -c Release` と `dotnet test` を実行する。
- [x] `tools/Artwork` はソリューションに含まれていないため、CIで個別にビルドして、壊れていないことを確認する。
- [x] `release.yml` と同じく、Actionはコミットハッシュで固定し、`permissions: contents: read`、`persist-credentials: false`、`timeout-minutes` を設定する。
- [x] CIのビルドで警告をエラーにする（`TreatWarningsAsErrors`）。既知の `NU1701` の扱いは G6 で決める。
- [x] 通信のテスト（テスト用PLCサーバーを使うもの）がCI上で安定して通る。不安定なら原因を直す（再試行で隠さない）。

**検証**: わざと失敗するテストを入れたブランチで、CIが失敗する。

**対象外**: 実機PLCを使うテスト。

---

## G3: プロジェクトファイルを別のPC・別の版で開ける

**Goal**: 保存した `*.psim` を、別のPCや別の利用者、将来の版でそのまま開ける。開けない場合は理由と対処が分かる。

**背景**: `DataSettings.CsvFolder` の既定値は利用者の「ドキュメント」の絶対パスで、プロジェクトファイルにそのまま保存される。別のPCで開くと存在しない場所を指し、ファイルに利用者名が残る。`ProjectSerializer.FromJson` は `FormatVersion` が現在の値（2）と違うと読込みを拒否するだけで、変換の仕組みがない。未知の項目（将来の版が追加した項目）は読込み時に捨てられ、古い版で上書き保存すると消える。

**対象**: `src/PidSimulator.Core/Project/ProjectDocument.cs`、`ProjectSerializer.cs`、`src/PidSimulator.App/ViewModels/MainViewModel.cs`、`SettingsViewModel.cs`、`docs/specification.md`、`tests/PidSimulator.Tests/ProjectTests.cs`

**完了条件**
- [x] CSV の保存先が既定のままなら、プロジェクトファイルに絶対パスを書かない（空欄＝既定の場所として保存し、読込み時に現在の利用者の既定へ解決する）。
- [x] 保存されている CSV フォルダーが存在しない・書き込めない場合は、出力時に既定の場所を提案する。
- [x] 形式番号ごとの変換の入口を用意する（形式 N → N+1 の変換を順に適用）。公開済みの版（v0.1.0〜v0.1.2）はすべて形式2なので、形式1からの変換は不要。
- [x] 現在より新しい形式のファイルを開いたときは、「新しい版のアプリで保存されたファイルです」と表示して開かない。
- [x] 未知の項目を保持し、保存時に書き戻す（`JsonExtensionData` など）。
- [x] 上記の単体テストがある（既定の保存先が絶対パスで書かれないこと、新しい形式の拒否、未知の項目の保持）。
- [x] `specification.md` のプロジェクト形式の説明を更新する。

**検証**: 既定設定で保存した `*.psim` をテキストで開き、利用者名を含むパスがないことを確認する。別の利用者アカウントで開いて CSV 出力ができる。

**対象外**: プロジェクト形式の変更そのもの（形式3の導入）。

---

## G4: PCのスリープや時計の変更で時刻と通信判定が狂わない

**Goal**: PCがスリープから復帰したあとや、システム時計が変更されたあとも、イベントログ・CSV の時刻が実時刻と一致し、通信の鮮度判定が誤らない。

**背景**: シミュレーション時刻（`SimulationEngine.T`）は周期ごとに 0.1 秒ずつ進み、表示時刻は「開始時刻＋T」で求める。演算が1秒を超えて遅れると遅れを捨てるため、スリープ中の時間は T に含まれない。復帰後のイベントログと CSV の時刻は、スリープした時間だけ過去にずれる。通信キャッシュ（`CachedPlcClient`）は値の鮮度を `DateTime.UtcNow` の差で判定しており、時計合わせで時刻が戻る・進むと、古い値を有効と見なしたり、正常な値を異常と見なしたりする。

**対象**: `src/PidSimulator.Core/SimulationEngine.cs`、`src/PidSimulator.Core/Plc/CachedPlcClient.cs`、`src/PidSimulator.Core/EventLog.cs`、`docs/specification.md`、`tests/PidSimulator.Tests/`

**完了条件**
- [x] スリープ復帰などで遅れを捨てたとき、表示時刻（イベントログ・CSV・トレンドの時刻軸）が実時刻に合うようにする。方法（開始時刻を補正する、遅れを記録して時刻変換に使う、など）を決め、仕様書に書く。
- [x] 遅れを捨てたことをイベントログに1件残す（「演算が N 秒中断しました」）。
- [x] モデルの計算は、スリープ中の時間を進めない（復帰後に値が飛ばない）。現在の動作を維持する。
- [x] 通信キャッシュの鮮度・再試行・未使用の判定を、時計の変更に影響されない経過時間（`Stopwatch` など）で行う。
- [x] 時刻を差し替えられる形にし、「時計が1時間戻っても、古い値を有効と判定しない」テストを追加する。

**検証**: 制御中にPCを数分スリープさせて復帰し、その後のイベントログと CSV の時刻が実時刻と一致する。

**対象外**: 演算周期（0.1 秒）の変更、実時間より速い・遅い再生。

---

## G5: 画面側（ViewModel）にテストがあり、大きなクラスが分かれている

**Goal**: 画面の操作手順（開く・保存・全制御開始・通信切替）に自動テストがあり、`MainViewModel` と `ControlTarget` の責務が分かれている。

**背景**: テストは Core と通信に集中しており（178件）、`PidSimulator.App` を参照するテストプロジェクトがない。`MainViewModel.cs` は660行で、プロジェクトの読込み・保存、未保存の確認、全制御開始、PLCの接続・切替、対象の登録・複製・削除を1つのクラスに持つ。`ControlTarget.cs` は661行で、運転状態、通信異常と復帰、FORCE、外乱の予定、プレビュー、トレンドを持つ。v0.1.1 の修正（停止中に対象を切り替えたときのトレンド表示範囲）は画面側の不具合だった。

**対象**: `src/PidSimulator.App/ViewModels/`、`src/PidSimulator.Core/ControlTarget.cs`、`tests/`（新規 `PidSimulator.App.Tests`）、`PidSimulator.sln`、`docs/build.md`

**完了条件**
- [x] `tests/PidSimulator.App.Tests` を追加する。ファイル選択・確認ダイアログは差し替えられる形にし、画面を表示せずにテストできる。
- [x] 次の手順のテストがある: 新規 → 登録 → 保存 → 開き直し、未保存のまま開く・閉じるときの確認、DEMO の上書き禁止、全制御開始（未接続なら接続してから開始）、制御中の通信切替の拒否、対象の複製と削除。
- [x] `MainViewModel` から、プロジェクトの読込み・保存と、PLCの接続・切替をそれぞれ別のクラスに分ける（目安400行以下）。
- [x] `ControlTarget` から、通信異常と復帰の判定、FORCE の管理を分ける。周期処理（`Cycle`）の順序と結果は変えない。
- [x] 既存のテストがそのまま通る。
- [x] `docs/build.md` のソース構成とテストの説明を更新する。

**検証**: 共通検証に新しいテストを加えて成功する。DEMO を開いて全制御開始し、分割前と同じ動作になる。

**対象外**: 画面デザインの変更、UI の自動操作テスト。

---

## G6: 依存関係とリリース用ファイルを整理する

**Goal**: アプリの依存関係に、使っていないものや説明のないものがない。同梱ランタイムの更新が滞らない。リリース用のファイルに未使用のものがない。

**背景**: `PidSimulator.App.csproj` は Windows 専用アプリだが、Linux 用のネイティブ資産（`SkiaSharp.NativeAssets.Linux.NoDependencies`、`HarfBuzzSharp.NativeAssets.Linux`）と OpenTK を直接参照している。`docs/build.md` には、ビルド時に `NU1701` 警告が出ると書かれている。ランタイムのバージョン（10.0.14）を csproj で固定しており、自己完結型で配布しているため、セキュリティ修正は新しいリリースを出さない限り利用者に届かない。`.github/scripts/render-release-notes.ps1` はどのワークフローからも呼ばれていない。

**対象**: `src/PidSimulator.App/PidSimulator.App.csproj`、`.github/scripts/`、`.github/workflows/`、`docs/build.md`、`docs/releasing.md`

**完了条件**
- [x] 直接参照している各パッケージについて、必要な理由を確認する。不要なら削除し、必要なら csproj のコメントに理由（どの警告・どの不具合を避けるためか）を書く。
- [x] `NU1701` 警告を解消する。解消できない場合は、対象のパッケージに限定して抑制し、理由を書く。
- [x] 発行したEXEのサイズと、トレンドの表示（描画・拡大縮小）が変更前と変わらないことを確認する。
- [x] 同梱ランタイムの扱いを決める。固定を続けるなら、古くなったことを知らせる定期ワークフローを用意する。外すなら、同梱されたバージョンをリリース本文かログに出す。
- [x] 使っていない `render-release-notes.ps1` を削除する（使うなら、呼び出し元を `releasing.md` に書く）。
- [x] 検査スクリプトは現在 plc-sim-slmp-dotnet と同一。可能なら [共通リリース標準](https://github.com/fa-yoshinobu/release_standard) の再利用可能ワークフローを呼ぶ形にして、コピーをなくす。

**検証**: `dotnet build` が警告なしで成功する。`build.bat` で作ったEXEで DEMO を動かし、トレンドが正しく描画される。

**対象外**: グラフのライブラリの変更。

---

## G7: 細かな整理

**Goal**: 下記の小さな問題がそれぞれ解消されている。項目ごとに独立して着手できる。

**完了条件**
- [x] **`build.bat` のURL**: エラーメッセージは「.NET 10 SDK」だが、URLが `/download/dotnet/9.0` のまま。10.0 に直す。
- [x] **CHANGELOG の形式**: 0.1.2 は英語・見出しつき、0.1.1 以前は日本語・見出しなしで、日付とリンク定義もない。形式を揃える。
- [x] **README**: 末尾の英語ブロックと日本語本文でダウンロードの説明が重複しないようにし、保守者向けの手順は `docs/releasing.md` へ移す。
- [x] **`.editorconfig`**: C# の書式を定義する（`.gitattributes` は既にある）。
- [x] **イベントログの保持**: 5,000件を超えると先頭から消える。消える前に知らせるか、CSV 出力の案内を表示する。
- [x] **握りつぶしの見直し**: `catch { }` の箇所（破棄時・ループ終了時）を、G1 のログへ記録する形に直す。

**検証**: 共通検証と、各項目の目視確認。
