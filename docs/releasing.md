# リリースとVirusTotal検査

ZIPをGitHub Releasesへ公開したあと、その中のEXEをVirusTotalで検査し、Release本文に検査結果へのリンクを表示します。

## APIキー

リポジトリの **Settings → Secrets and variables → Actions** にあるRepository secret **`VT_API_KEY`** を使用します。未登録ならVirusTotalのAPIキーをこの名前で登録してください。ワークフローは検査ステップだけに環境変数 `VIRUSTOTAL_API_KEY` として渡します。キーをコードやRelease本文に記載する必要はありません。[GitHubのSecret設定手順](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets)

VirusTotalへの通常の送信は公開サンプルの投稿です。検査結果や送信ファイルはパートナー・利用者と共有され得ます。検査するのは公開したEXEだけで、`DEMO.psim`、個人のプロジェクト、ソースコードは投稿しません。[VirusTotalの共有について](https://docs.virustotal.com/docs/how-it-works)

## ZIPを公開する

1. リリース対象の変更をcommitしてGitHubへpushします。
2. 未使用のバージョンタグを作成してpushします。

   ```powershell
   git tag v0.1.1
   git push origin v0.1.1
   ```

3. **Actions → Release** で進捗を確認します。

[Releaseワークフロー](../.github/workflows/release.yml)は.NET 10でテスト・発行後、`PidSimulator-win-x64.zip` と `PidSimulator-win-x64.zip.sha256` を公開します。ZIPの内容は **`PidSimulator.exe` と `DEMO.psim` の2ファイルだけ**です。

タグは `vMAJOR.MINOR.PATCH` または `vMAJOR.MINOR.PATCH-rc.1` などの形式を使用します。EXEのバージョンもタグに合わせます。ハイフン付きのタグはGitHubのプレリリースとして公開します。作成済みのタグから公開する場合は **Actions → Release → Run workflow** の `release_tag` にそのタグを指定します。

公開時の本文にはダウンロードリンク・起動方法と「VirusTotal：検査中。」を表示します。公開後のジョブがVirusTotalの検査ワークフローを呼び、完了すると検査中の表示を結果リンクに置き換えます。

本文の定型文は [`.github/release-notes.md`](../.github/release-notes.md) で管理し、GitHub ActionsがダウンロードURLとVirusTotalのリンクを埋め込みます。検査が完了している既存リリースの本文だけを更新する場合は、**Actions → Update English release notes → Run workflow** で `release_tag` を指定します。配布ファイルやタグを変更せず、VirusTotalの再検査も行いません。

## 公開後の検査と再検査

[VirusTotal検査ワークフロー](../.github/workflows/virustotal-release.yml)は次の3通りで実行します。

- 上記のReleaseワークフローがZIPを公開したあとに呼び出す。
- GitHubの画面などからReleaseを公開したときの `release: published` イベント。
- **Actions → Scan release with VirusTotal → Run workflow** で公開済みの `release_tag` を指定して再検査する。

公開済みのEXE、または公開ZIP内のEXEを取得して検査します。このアプリの配布ZIPでは `PidSimulator.exe` が対象です。再検査では本文内の検査欄だけを置き換えます。

検査完了後の本文にはVirusTotalの検査結果へのリンクを表示し、検出数や判定はリンク先で確認できます。検査日時やSHA-256などの詳細はActionsの検査レポートに残します。

キー未設定・APIエラー・タイムアウトなどで検査が完了しない場合は、本文に「検査未完了」と表示してワークフローを失敗にします。Actionsのログを確認し、原因を解消してから再検査を実行してください。検査結果や検査未完了によって、ReleaseやZIPの公開状態は変更しません。

検査JSON・MarkdownはActionsの `virustotal-実行ID-試行番号` artifactへ30日間保存します。Releaseの添付は配布ZIPとそのSHA-256です。

## 使用するVirusTotal API

[VirusTotal API v3のファイル送信](https://docs.virustotal.com/reference/files-scan)を使用します。32 MBを超えるEXEは[大容量ファイル用のアップロードURL](https://docs.virustotal.com/reference/files-upload-url)を取得して送信し、この経路の上限は650 MBです。自己完結EXEは32 MBを超えるため、大容量送信に対応しています。APIキーの利用枠や契約による制約も適用されます。

固定名ZIPとSHA-256による配布を適用済みです。旧リリースのAssetは変更しません。本文更新ワークフローも新形式のAssetがあるリリースだけを対象にします。

## 同梱ランタイムの更新確認

自己完結EXEの再現性を保つため、アプリcsprojの `Microsoft.NETCore.App` と `Microsoft.WindowsDesktop.App.WPF` の `FrameworkReference` にある `RuntimeFrameworkVersion` を固定します。利用者がPCの.NETを更新しても、同梱ランタイムは更新されません。

[Check bundled runtime](../.github/workflows/check-runtime-update.yml) は毎週水曜日と手動起動で、Microsoftの[公式.NETリリースメタデータ](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)を確認します。2つの固定値をそれぞれ.NET Runtime/Windows Desktop Runtimeの同じメジャー・マイナーの最新安定版と比較します。プレビューは対象外です。更新がある場合はSummaryとログに表示して終了コード1で失敗させ、GitHub Actionsの失敗通知で気付けるようにします。取得・設定エラーは終了コード2で区別します。リポジトリや公開物の自動変更は行いません。

ローカルで確認する場合：

```powershell
python -B .github/scripts/check_runtime_update.py
```

更新時は両FrameworkReferenceの固定値を合わせ、ビルド・テスト・発行EXEのトレンド描画と拡大縮小を確認し、CHANGELOGと必要なリリース本文を更新して新しい版を公開します。公開済みZIPの差替えは行いません。

本文の生成は `release_assets.py` に統一し、未使用の `render-release-notes.ps1` は削除しました。検査処理は現在各リポジトリ内のスクリプトを使用しています。共通仕様リポジトリで利用可能な再利用ワークフローを確認できないため、既存の検査スクリプトを維持します。


## 配布文面の保守

新規リリースの前に、CHANGELOGの該当項目を基に `.github/release-notes.md` のChangesを英語で更新します。本文・VirusTotal CIの表示は英語に統一し、READMEや内部資料の言語は維持します。[共通リリース標準](https://github.com/fa-yoshinobu/release_standard)も参照してください。

READMEの最新版リンクは固定名ZIPとSHA-256を指します。各リリース本文はそのタグの配布物を指し、旧リリースのAssetは変更しません。ローカルの配布物は `artifacts/release/` の `PidSimulator.exe`・`DEMO.psim`・ZIP・SHA-256です。
