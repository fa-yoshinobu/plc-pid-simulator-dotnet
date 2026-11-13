from http.client import IncompleteRead
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / ".github" / "scripts"))
import check_runtime_update as checker


def metadata(runtime="10.0.15", desktop="10.0.15"):
    return {"channel-version": "10.0", "releases": [
        {"runtime": {"version": "10.0.16-preview.1"}, "windowsdesktop": {"version": "10.0.16-preview.1"}},
        {"runtime": {"version": runtime}, "windowsdesktop": {"version": desktop}},
        {"runtime": {"version": "10.0.9"}, "windowsdesktop": {"version": "10.0.9"}},
    ]}


class RuntimeUpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name) / "App.csproj"
        self.project.write_text('<Project><ItemGroup>'
            '<FrameworkReference Update="Microsoft.NETCore.App" RuntimeFrameworkVersion="10.0.14" />'
            '<FrameworkReference Update="Microsoft.WindowsDesktop.App.WPF" RuntimeFrameworkVersion="10.0.14" />'
            '</ItemGroup></Project>', encoding="utf-8")

    def run_checker(self, response):
        with patch.object(checker, "urlopen", return_value=io.BytesIO(json.dumps(response).encode())) as request, \
                patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": ""}), patch("sys.stdout", new_callable=io.StringIO) as output:
            code = checker.main(["--project", str(self.project)])
        self.assertEqual(request.call_args.args[0], "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json")
        self.assertEqual(request.call_args.kwargs["timeout"], 30)
        return code, output.getvalue()

    def test_reads_both_framework_reference_attributes_and_reports_new_stable_patch(self):
        self.assertEqual(set(checker.read_pins(self.project)), set(checker.FRAMEWORKS))
        code, output = self.run_checker(metadata())
        self.assertEqual(1, code)
        self.assertIn("10.0.14 | 10.0.15 | yes", output)
        self.assertNotIn("preview", output)

    def test_up_to_date_pins_succeed_and_use_numeric_patch_comparison(self):
        code, output = self.run_checker(metadata("10.0.14", "10.0.14"))
        self.assertEqual(0, code)
        self.assertIn("No newer stable patch", output)

    def test_desktop_update_is_detected_independently(self):
        rows = checker.compare_versions(checker.read_pins(self.project), metadata("10.0.14", "10.0.15"))
        self.assertFalse(rows[0]["outdated"])
        self.assertTrue(rows[1]["outdated"])

    def test_invalid_or_missing_pins_are_configuration_errors(self):
        for text in ('<Project />', '<Project><FrameworkReference Update="Microsoft.NETCore.App" RuntimeFrameworkVersion="10.0.*" /></Project>'):
            with self.subTest(text=text):
                self.project.write_text(text, encoding="utf-8")
                with patch.object(checker, "urlopen") as request, patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": ""}), patch("sys.stdout", new_callable=io.StringIO):
                    self.assertEqual(2, checker.main(["--project", str(self.project)]))
                request.assert_not_called()

    def test_wrong_channel_or_missing_component_is_not_an_up_to_date_result(self):
        for response in ({"channel-version": "11.0", "releases": []},
                         {"channel-version": "10.0", "releases": [{"runtime": {"version": "10.0.15"}}]}):
            with self.subTest(response=response):
                code, output = self.run_checker(response)
                self.assertEqual(2, code)
                self.assertIn("check error", output)

    def test_network_failure_is_distinct_from_a_runtime_update(self):
        for error in (OSError("offline"), IncompleteRead(b"partial")):
            with self.subTest(error=type(error).__name__), patch.object(checker, "urlopen", side_effect=error), \
                    patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": ""}), patch("sys.stdout", new_callable=io.StringIO) as output:
                self.assertEqual(2, checker.main(["--project", str(self.project)]))
            self.assertIn("check error", output.getvalue())

    def test_workflow_summary_preserves_the_update_result(self):
        summary = Path(self.temp.name) / "summary.md"
        with patch.object(checker, "urlopen", return_value=io.BytesIO(json.dumps(metadata()).encode())), \
                patch.dict(os.environ, {"GITHUB_STEP_SUMMARY": str(summary)}), patch("sys.stdout", new_callable=io.StringIO):
            self.assertEqual(1, checker.main(["--project", str(self.project)]))
        self.assertIn("Microsoft.WindowsDesktop.App.WPF | 10.0.14 | 10.0.15 | yes", summary.read_text(encoding="utf-8"))
