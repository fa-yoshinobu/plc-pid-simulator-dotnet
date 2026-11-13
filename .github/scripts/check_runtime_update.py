"""Report newer stable patches for the two runtimes bundled with the application."""

import argparse
from http.client import HTTPException
import json
import os
from pathlib import Path
import re
from urllib.request import urlopen
import xml.etree.ElementTree as ET

FRAMEWORKS = {
    "Microsoft.NETCore.App": "runtime",
    "Microsoft.WindowsDesktop.App.WPF": "windowsdesktop",
}


def version_tuple(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", value):
        raise ValueError("Runtime pins and stable versions must use MAJOR.MINOR.PATCH.")
    return tuple(int(part) for part in value.split("."))


def read_pins(project):
    pins = {}
    for item in ET.parse(project).getroot().iter("FrameworkReference"):
        name = item.get("Update")
        if name in FRAMEWORKS:
            if name in pins:
                raise ValueError("Duplicate bundled runtime framework reference.")
            version = item.get("RuntimeFrameworkVersion")
            version_tuple(version)
            pins[name] = version
    if pins.keys() != FRAMEWORKS.keys():
        raise ValueError("Both bundled runtime framework pins are required.")
    if len({version_tuple(value)[:2] for value in pins.values()}) != 1:
        raise ValueError("Both bundled runtimes must use the same major/minor channel.")
    return pins


def compare_versions(pins, metadata):
    channel = ".".join(pins["Microsoft.NETCore.App"].split(".")[:2])
    if metadata.get("channel-version") != channel:
        raise ValueError("The official runtime metadata channel does not match the pins.")
    rows = []
    for name, component in FRAMEWORKS.items():
        candidates = []
        for release in metadata["releases"]:
            version = release.get(component, {}).get("version")
            if isinstance(version, str) and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
                if version_tuple(version)[:2] == version_tuple(pins[name])[:2]:
                    candidates.append((version_tuple(version), version))
        if not candidates:
            raise ValueError("No stable runtime version was found for " + name + ".")
        latest = max(candidates)[1]
        rows.append({"framework": name, "pinned": pins[name], "latest": latest,
                     "outdated": version_tuple(pins[name]) < version_tuple(latest)})
    return rows


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=Path("src/PidSimulator.App/PidSimulator.App.csproj"))
    args = parser.parse_args(argv)
    try:
        pins = read_pins(args.project)
        channel = ".".join(pins["Microsoft.NETCore.App"].split(".")[:2])
        url = f"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json"
        with urlopen(url, timeout=30) as response:
            rows = compare_versions(pins, json.load(response))
        lines = ["## Bundled runtime update check", "", f"Official metadata: {url}", "",
                 "| Framework | Pinned | Latest stable | Update available |", "| --- | --- | --- |"]
        for row in rows:
            lines.append(f"| {row['framework']} | {row['pinned']} | {row['latest']} | {'yes' if row['outdated'] else 'no'} |")
        outdated = any(row["outdated"] for row in rows)
        lines += ["", "Newer runtime patches are available. Review and update both framework pins, then validate a new application release."
                  if outdated else "No newer stable patch is available for the pinned runtime channel."]
        code = 1 if outdated else 0
    except (OSError, HTTPException, ValueError, TypeError, KeyError, AttributeError, ET.ParseError):
        lines = ["## Bundled runtime update check", "", "Unable to read the runtime pins or official release metadata. This is a check error, not an update result."]
        code = 2
    report = "\n".join(lines) + "\n"
    print(report, end="")
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with Path(summary).open("a", encoding="utf-8") as stream:
            stream.write(report)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
