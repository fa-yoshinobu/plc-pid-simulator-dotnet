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

[Releaseワークフロー](../.github/workflows/release.yml)は.NET 9でテスト・発行後、`PidSimulator-v0.1.1-win-x64.zip` を公開します。ZIPの内容は **`PidSimulator.exe` と `DEMO.psim` の2ファイルだけ**です。

タグは `vMAJOR.MINOR.PATCH` または `vMAJOR.MINOR.PATCH-rc.1` などの形式を使用します。EXEのバージョンもタグに合わせます。ハイフン付きのタグはGitHubのプレリリースとして公開します。作成済みのタグから公開する場合は **Actions → Release → Run workflow** の `release_tag` にそのタグを指定します。

公開時の本文にはダウンロードリンク・起動方法と「VirusTotal：検査中。」を表示します。公開後のジョブがVirusTotalの検査ワークフローを呼び、完了すると検査中の表示を結果リンクに置き換えます。

本文の定型文は [`.github/release-notes.md`](../.github/release-notes.md) で管理し、GitHub ActionsがダウンロードURLとVirusTotalのリンクを埋め込みます。検査が完了している既存リリースの本文だけを更新する場合は、**Actions → Update release notes → Run workflow** で `release_tag` を指定します。配布ファイルやタグを変更せず、VirusTotalの再検査も行いません。

## 公開後の検査と再検査

[VirusTotal検査ワークフロー](../.github/workflows/virustotal-release.yml)は次の3通りで実行します。

- 上記のReleaseワークフローがZIPを公開したあとに呼び出す。
- GitHubの画面などからReleaseを公開したときの `release: published` イベント。
- **Actions → Check published release with VirusTotal → Run workflow** で公開済みの `release_tag` を指定して再検査する。

公開済みのEXE、または公開ZIP内のEXEを取得して検査します。このアプリの配布ZIPでは `PidSimulator.exe` が対象です。再検査では本文内の検査欄だけを置き換えます。

検査完了後の本文にはVirusTotalの検査結果へのリンクを表示し、検出数や判定はリンク先で確認できます。検査日時やSHA-256などの詳細はActionsの検査レポートに残します。

キー未設定・APIエラー・タイムアウトなどで検査が完了しない場合は、本文に「検査未完了」と表示してワークフローを失敗にします。Actionsのログを確認し、原因を解消してから再検査を実行してください。検査結果や検査未完了によって、ReleaseやZIPの公開状態は変更しません。

検査JSON・MarkdownはActionsの `virustotal-実行ID-試行番号` artifactへ30日間保存します。Releaseの添付は配布ZIPだけです。

## 使用するVirusTotal API

[VirusTotal API v3のファイル送信](https://docs.virustotal.com/reference/files-scan)を使用します。32 MBを超えるEXEは[大容量ファイル用のアップロードURL](https://docs.virustotal.com/reference/files-upload-url)を取得して送信し、この経路の上限は650 MBです。自己完結EXEは32 MBを超えるため、大容量送信に対応しています。APIキーの利用枠や契約による制約も適用されます。
