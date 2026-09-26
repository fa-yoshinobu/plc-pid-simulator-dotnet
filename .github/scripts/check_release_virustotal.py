"""Scan already-published release executables and update only a managed body block."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
from urllib.parse import quote
import zipfile


MAX_BYTES = 650_000_000
START = "<!-- virustotal:start -->"
END = "<!-- virustotal:end -->"
SCANNER = Path(__file__).with_name("scan_virustotal.py")


class CheckError(Exception):
    """An intentionally public error message without credentials or local paths."""


def copy_limited(source, destination, limit=MAX_BYTES):
    total = 0
    while chunk := source.read(min(1024 * 1024, limit - total + 1)):
        total += len(chunk)
        if total > limit:
            raise CheckError("対象ファイルが650 MBの上限を超えています。")
        destination.write(chunk)
    return total


def replace_block(body: str, content: str) -> str:
    block = f"{START}\n{content.strip()}\n{END}"
    if START not in body and END not in body:
        return body + ("\n\n" if body else "") + block + "\n"
    if body.count(START) != 1 or body.count(END) != 1 or body.index(START) > body.index(END):
        raise CheckError("Release本文のVirusTotal管理区間が不正なため、自動更新できません。")
    return body[:body.index(START)] + block + body[body.index(END) + len(END):]


def markdown_label(value: str) -> str:
    # These are public asset names; keep them inert in the release's Markdown.
    value = re.sub(r"[\x00-\x1f\x7f]", " ", value)
    return re.sub(r"([\\`*_{}\[\]()<>#!|])", r"\\\1", value)


class GitHub:
    def __init__(self, repository: str, output: Path):
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
            raise CheckError("repositoryはowner/repo形式で指定してください。")
        self.repository = repository
        self.work = output / "work"
        self.work.mkdir(parents=True, exist_ok=True)

    def api(self, endpoint: str, payload=None):
        args = ["gh", "api", endpoint]
        if payload is not None:
            body_file = self.work / "release-body.json"
            body_file.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
            args.extend(["--method", "PATCH", "--input", str(body_file)])
        try:
            result = subprocess.run(args, capture_output=True, check=False)
        except OSError as exc:
            raise CheckError("GitHub CLIを起動できませんでした。") from exc
        if result.returncode:
            raise CheckError("GitHub APIへのアクセスに失敗しました。")
        try:
            return json.loads(result.stdout)
        except (ValueError, UnicodeError) as exc:
            raise CheckError("GitHub APIの応答を読み取れませんでした。") from exc

    def release(self, tag: str):
        release = self.api(f"repos/{self.repository}/releases/tags/{quote(tag, safe='')}")
        if not isinstance(release, dict) or release.get("draft") is not False or not release.get("published_at"):
            raise CheckError("公開済みのReleaseだけを検査できます。")
        if not isinstance(release.get("id"), int):
            raise CheckError("Releaseの識別情報を確認できませんでした。")
        return release

    def update_body(self, tag: str, release_id: int, content: str):
        # Fetch again so edits made while the scan was running are preserved.
        current = self.release(tag)
        if current["id"] != release_id:
            raise CheckError("検査中にReleaseが置き換わったため、本文を更新しませんでした。")
        body = replace_block(current.get("body") or "", content)
        self.api(f"repos/{self.repository}/releases/{release_id}", {"body": body})

    def download(self, asset: dict, destination: Path):
        size, asset_id = asset.get("size"), asset.get("id")
        if type(size) is not int or not 0 < size <= MAX_BYTES or type(asset_id) is not int or asset_id <= 0:
            raise CheckError("Release assetのサイズまたは識別情報が不正です（上限650 MB）。")
        args = ["gh", "api", f"repos/{self.repository}/releases/assets/{asset_id}",
                "--header", "Accept: application/octet-stream"]
        try:
            with subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as process:
                try:
                    with destination.open("wb") as stream:
                        actual = copy_limited(process.stdout, stream)
                    returncode = process.wait()
                except Exception:
                    process.kill()
                    process.wait()
                    raise
        except OSError as exc:
            raise CheckError("公開ファイルのダウンロードに失敗しました。") from exc
        if returncode or actual != size:
            raise CheckError("公開ファイルを正しくダウンロードできませんでした。")


def executable_files(github: GitHub, assets: list):
    for asset in assets:
        name = asset.get("name", "")
        if not isinstance(name, str) or not name.lower().endswith((".exe", ".zip")):
            continue
        asset_id = asset.get("id")
        if type(asset_id) is not int or asset_id <= 0:
            raise CheckError("Release assetの識別情報が不正です。")
        destination = github.work / f"asset-{asset_id}.download"
        github.download(asset, destination)
        if name.lower().endswith(".exe"):
            executable = github.work / f"asset-{asset_id}.exe"
            destination.replace(executable)
            yield name, executable
            continue
        try:
            with zipfile.ZipFile(destination) as archive:
                for index, entry in enumerate(archive.infolist()):
                    if entry.is_dir() or not entry.filename.lower().endswith(".exe"):
                        continue
                    if stat.S_ISLNK(entry.external_attr >> 16):
                        raise CheckError("ZIP内のEXEがシンボリックリンクのため検査できません。")
                    if not 0 < entry.file_size <= MAX_BYTES:
                        raise CheckError("ZIP内のEXEが650 MBの上限を超えているか空です。")
                    # Never use the ZIP path to form a local destination, including ../.
                    executable = github.work / f"asset-{asset_id}-member-{index}.exe"
                    with archive.open(entry) as source, executable.open("wb") as stream:
                        size = copy_limited(source, stream)
                    if size != entry.file_size:
                        raise CheckError("ZIP内のEXEを正しく読み取れませんでした。")
                    yield f"{name} / {entry.filename}", executable
        except (zipfile.BadZipFile, RuntimeError, NotImplementedError, OSError) as exc:
            raise CheckError("公開ZIPからEXEを読み取れませんでした。") from exc


def file_sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def scan_file(path: Path, sha256: str, reports: Path) -> dict:
    folder = reports / sha256
    folder.mkdir(parents=True, exist_ok=True)
    url = f"https://www.virustotal.com/gui/file/{sha256}/detection"
    record = {"sha256": sha256, "url": url, "complete": False, "stats": {}, "error": ""}
    try:
        process = subprocess.run(
            [sys.executable, str(SCANNER), "--file", str(path), "--output-dir", str(folder),
             "--timeout-seconds", "1800", "--poll-seconds", "30"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False, timeout=1900)
        raw = json.loads((folder / "virustotal.json").read_text(encoding="utf-8"))
        if raw.get("sha256") is not None and raw["sha256"] != sha256:
            raise CheckError("検査結果のSHA-256が公開ファイルと一致しませんでした。")
        record["complete"] = (process.returncode == 0 and raw.get("sha256") == sha256
                              and raw.get("verdict") == "completed" and raw.get("status") == "completed")
        stats = raw.get("stats", {})
        if isinstance(stats, dict):
            record["stats"] = {key: value for key, value in stats.items()
                               if re.fullmatch(r"[a-z_-]+", key) and type(value) is int and value >= 0}
        if isinstance(raw.get("checked_at"), str):
            record["checked_at"] = raw["checked_at"]
        if not record["complete"]:
            record["error"] = "VirusTotalの検査が完了しませんでした。APIキー・上限・実行時間を確認してください。"
    except CheckError as exc:
        record["error"] = str(exc)
    except (OSError, ValueError, UnicodeError, TypeError, AttributeError, subprocess.TimeoutExpired):
        record["error"] = "VirusTotal検査を実行または結果を取得できませんでした。"
    return record


def asset_signature(assets: list) -> list:
    relevant = [(asset.get("id"), asset.get("name"), asset.get("size"), asset.get("updated_at"))
                for asset in assets if isinstance(asset.get("name"), str)
                and asset["name"].lower().endswith((".exe", ".zip"))]
    return sorted(relevant, key=lambda item: (str(item[0]), item[1]))


def run_link(repository: str) -> str:
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    if run_id.isdecimal():
        return f"[Actionsの実行結果](https://github.com/{repository}/actions/runs/{run_id})"
    return "[Actionsの実行結果](https://github.com/" + repository + "/actions)"


def report_markdown(entries: list, errors: list, link: str) -> str:
    complete = bool(entries) and not errors and all(entry["complete"] for entry in entries)
    lines = ["**VirusTotal検査済み**" if complete else "**VirusTotal 検査未完了**", "", link]
    for entry in entries:
        lines.extend(["", "### " + markdown_label(entry["name"])])
        if entry["complete"]:
            stats = entry["stats"]
            malicious, suspicious = stats.get("malicious", 0), stats.get("suspicious", 0)
            lines.append(f"検査完了：悪意あり {malicious}、疑わしい {suspicious}（結果合計 {sum(stats.values())}）。")
        else:
            lines.append("検査未完了。")
        if entry.get("obsolete"):
            lines.append("公開ファイルが変更されたため、以下は変更前の参考結果です。再検査が必要です。")
        lines.append(f"[VirusTotalの検査結果を見る]({entry['url']})")
        if entry.get("checked_at"):
            lines.append("結果取得日時（UTC）: " + markdown_label(entry["checked_at"]))
        lines.append(f"SHA-256: `{entry['sha256']}`")
    if errors:
        lines.extend(["", *["- " + markdown_label(error) for error in errors]])
    lines.extend(["", "検査時点の結果です。検査結果や検査未完了を理由に、Releaseの公開状態・配布ファイルは変更しません。"])
    return "\n".join(lines)


def release_markdown(entries: list, errors: list) -> str:
    complete = bool(entries) and not errors and all(entry["complete"] for entry in entries)
    lines = [] if complete else ["VirusTotal：検査未完了。"]
    current = [entry for entry in entries if not entry.get("obsolete")]
    for entry in current:
        label = "VirusTotalの検査結果"
        if len(entries) > 1:
            label += "（" + markdown_label(entry["name"]) + "）"
        lines.append(f"[{label}]({entry['url']})")
    return "\n\n".join(lines)


def check_release(repository: str, tag: str, output: Path, github=None) -> int:
    reports = output / "reports"
    reports.mkdir(parents=True, exist_ok=True)
    entries, errors, seen = [], [], {}
    release_id = None
    original_assets = None
    link = run_link(repository)
    try:
        github = github or GitHub(repository, output)
        if not os.environ.get("GH_TOKEN"):
            raise CheckError("GH_TOKENが設定されていません。")
        release = github.release(tag)
        release_id = release["id"]
        github.update_body(tag, release_id, "VirusTotal：検査中。")
        assets = release.get("assets", [])
        if not isinstance(assets, list):
            raise CheckError("Releaseの配布ファイル一覧を取得できませんでした。")
        original_assets = asset_signature(assets)
        for name, executable in executable_files(github, assets):
            sha256 = file_sha256(executable)
            if sha256 not in seen:
                seen[sha256] = scan_file(executable, sha256, reports)
            entries.append({"name": name, **seen[sha256]})
        if not entries:
            raise CheckError("公開Releaseに検査対象のEXEが見つかりませんでした。")
    except CheckError as exc:
        errors.append(str(exc))
    except Exception:
        # Do not expose API bodies, tokens, ZIP parser details, or machine paths.
        errors.append("公開Releaseの検査処理でエラーが発生しました。")
    if any(not entry["complete"] for entry in entries):
        errors.append("一部のEXEのVirusTotal検査が完了しませんでした。")
    if original_assets is not None:
        try:
            current = github.release(tag)
            if current["id"] != release_id or asset_signature(current.get("assets", [])) != original_assets:
                errors.append("検査中に公開EXEまたはZIPが変更されました。公開ファイル変更後は再検査してください。")
                for entry in entries:
                    entry["complete"] = False
                    entry["obsolete"] = True
        except Exception:
            errors.append("検査完了時に公開ファイルが変更されていないことを確認できませんでした。")
    content = report_markdown(entries, errors, link)
    if release_id is not None:
        try:
            github.update_body(tag, release_id, release_markdown(entries, errors))
        except Exception:
            errors.append("Release本文への検査結果の反映に失敗しました。")
            content = report_markdown(entries, errors, link)
    complete = bool(entries) and not errors and all(entry["complete"] for entry in entries)
    (reports / "release-virustotal.json").write_text(
        json.dumps({"repository": repository, "release_tag": tag, "complete": complete,
                    "executables": entries, "errors": errors}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (reports / "release-virustotal.md").write_text(content + "\n", encoding="utf-8")
    print("VirusTotal release check: completed." if complete else "VirusTotal release check: incomplete; see the report artifact.")
    return 0 if complete else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--release-tag", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    return check_release(args.repository, args.release_tag, args.output_dir)


if __name__ == "__main__":
    raise SystemExit(main())
