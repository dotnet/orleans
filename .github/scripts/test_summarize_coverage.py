import importlib.util
from pathlib import Path
import tempfile
import unittest


SCRIPT_PATH = Path(__file__).with_name("summarize-coverage.py")
SPEC = importlib.util.spec_from_file_location("summarize_coverage", SCRIPT_PATH)
SUMMARIZE_COVERAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SUMMARIZE_COVERAGE)


class ReadReportTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        self.source_root = self.root / "src"
        self.source_root.mkdir()

    def tearDown(self):
        self.temporary_directory.cleanup()

    def test_read_report_accepts_valid_utf8(self):
        source_file = self.source_root / "Example.cs"
        report = self.root / "coverage.cobertura.xml"
        report.write_text(self._report_xml(source_file), encoding="utf-8")

        measured_lines = SUMMARIZE_COVERAGE.read_report(report, self.source_root)

        self.assertEqual(
            {("src/Example.cs", 10): True, ("src/Example.cs", 11): False},
            measured_lines,
        )

    def test_read_report_rejects_utf16_with_doctype(self):
        report = self.root / "coverage.cobertura.xml"
        report.write_text(
            '<!DOCTYPE coverage [<!ENTITY payload "expanded">]><coverage />',
            encoding="utf-16",
        )

        with self.assertRaisesRegex(RuntimeError, "must contain valid UTF-8"):
            SUMMARIZE_COVERAGE.read_report(report, self.source_root)

    def test_read_report_rejects_invalid_utf8(self):
        report = self.root / "coverage.cobertura.xml"
        report.write_bytes(b"\xff\xfe\x00\x80")

        with self.assertRaisesRegex(RuntimeError, "must contain valid UTF-8"):
            SUMMARIZE_COVERAGE.read_report(report, self.source_root)

    def test_read_report_rejects_unsupported_xml_declarations(self):
        declarations = (
            "<!DOCTYPE coverage>",
            '<!ENTITY payload "expanded">',
        )

        for declaration in declarations:
            with self.subTest(declaration=declaration):
                report = self.root / "coverage.cobertura.xml"
                report.write_text(
                    f"{declaration}<coverage />",
                    encoding="utf-8",
                )

                with self.assertRaisesRegex(
                    RuntimeError, "unsupported XML declarations"
                ):
                    SUMMARIZE_COVERAGE.read_report(report, self.source_root)

    def test_read_report_rejects_symbolic_link(self):
        target = self.root / "target.cobertura.xml"
        target.write_text(
            self._report_xml(self.source_root / "Example.cs"), encoding="utf-8"
        )
        report = self.root / "coverage.cobertura.xml"
        report.symlink_to(target)

        with self.assertRaisesRegex(RuntimeError, "must not be a symbolic link"):
            SUMMARIZE_COVERAGE.read_report(report, self.source_root)

    @staticmethod
    def _report_xml(source_file):
        return f"""
<coverage>
  <packages>
    <package>
      <classes>
        <class filename="{source_file}">
          <lines>
            <line number="10" hits="1" />
            <line number="11" hits="0" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"""


if __name__ == "__main__":
    unittest.main()
