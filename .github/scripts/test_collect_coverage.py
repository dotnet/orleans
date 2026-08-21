import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock


SCRIPT_PATH = Path(__file__).with_name("collect-coverage.py")
SPEC = importlib.util.spec_from_file_location("collect_coverage", SCRIPT_PATH)
COLLECT_COVERAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COLLECT_COVERAGE)


class GetSelectedModulesTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.repository_root = Path(self.temporary_directory.name)
        self.results_directory = self.repository_root / "results"
        self.results_directory.mkdir()
        self.module = (
            self.repository_root
            / "test"
            / "Example.Tests"
            / "bin"
            / "Debug"
            / "net10.0"
            / "Example.Tests.dll"
        )
        self.module.parent.mkdir(parents=True)
        self.module.touch()
        self.module.with_suffix(".testconfig.json").touch()

    def tearDown(self):
        self.temporary_directory.cleanup()

    def test_get_selected_modules_returns_module_with_tests(self):
        self._write_trx("selected.trx", total=3, code_base=self.module)

        modules = COLLECT_COVERAGE.get_selected_modules(
            self.results_directory, self.repository_root
        )

        self.assertEqual([self.module], modules)

    def test_get_selected_modules_skips_zero_test_module(self):
        self._write_trx("zero.trx", total=0, code_base=self.module)
        selected_module = self.module.with_name("Selected.Tests.dll")
        selected_module.touch()
        selected_module.with_suffix(".testconfig.json").touch()
        self._write_trx("selected.trx", total=1, code_base=selected_module)

        modules = COLLECT_COVERAGE.get_selected_modules(
            self.results_directory, self.repository_root
        )

        self.assertEqual([selected_module], modules)

    def test_get_selected_modules_rejects_outside_module(self):
        outside_module = self.repository_root / "outside" / "Example.Tests.dll"
        outside_module.parent.mkdir()
        outside_module.touch()
        self._write_trx("outside.trx", total=1, code_base=outside_module)

        with self.assertRaisesRegex(RuntimeError, "outside test/"):
            COLLECT_COVERAGE.get_selected_modules(
                self.results_directory, self.repository_root
            )

    def test_get_selected_modules_rejects_symlink_module(self):
        target = self.module
        symlink_module = target.with_name("Symlink.Tests.dll")
        symlink_module.symlink_to(target)
        symlink_module.with_suffix(".testconfig.json").touch()
        self._write_trx("symlink.trx", total=1, code_base=symlink_module)

        with self.assertRaisesRegex(RuntimeError, "symbolic link"):
            COLLECT_COVERAGE.get_selected_modules(
                self.results_directory, self.repository_root
            )

    def test_read_trx_rejects_utf16_with_doctype(self):
        trx_path = self.results_directory / "utf16.trx"
        trx_path.write_text(
            '<!DOCTYPE TestRun [<!ENTITY payload "expanded">]><TestRun />',
            encoding="utf-16",
        )

        with self.assertRaisesRegex(RuntimeError, "must contain valid UTF-8"):
            COLLECT_COVERAGE.read_trx(trx_path)

    def test_read_trx_rejects_symbolic_link(self):
        target = self.results_directory / "target.trx"
        self._write_trx(target.name, total=1, code_base=self.module)
        trx_path = self.results_directory / "symlink.trx"
        trx_path.symlink_to(target)

        with self.assertRaisesRegex(RuntimeError, "must not be a symbolic link"):
            COLLECT_COVERAGE.read_trx(trx_path)

    @mock.patch.object(COLLECT_COVERAGE.subprocess, "run")
    def test_collect_coverage_runs_only_selected_modules(self, run):
        self._write_trx("selected.trx", total=3, code_base=self.module)
        self._write_trx("zero.trx", total=0, code_base=self.module)
        settings_path = self.repository_root / ".github" / "coverage.config.xml"
        settings_path.parent.mkdir()
        settings_path.touch()

        COLLECT_COVERAGE.collect_coverage(
            self.repository_root, self.results_directory, "BVT"
        )

        run.assert_called_once()
        command = run.call_args.args[0]
        self.assertEqual(["dotnet", "exec", str(self.module)], command[:3])
        self.assertIn("--coverage", command)
        self.assertNotIn("--report-trx", command)
        output_index = command.index("--coverage-output")
        self.assertEqual(".coverage", Path(command[output_index + 1]).suffix)
        format_index = command.index("--coverage-output-format")
        self.assertEqual("coverage", command[format_index + 1])
        self.assertTrue(run.call_args.kwargs["check"])

    def _write_trx(self, filename, total, code_base):
        (self.results_directory / filename).write_text(
            f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="{COLLECT_COVERAGE.TRX_NAMESPACE}">
  <ResultSummary>
    <Counters total="{total}" />
  </ResultSummary>
  <TestDefinitions>
    <UnitTest>
      <TestMethod codeBase="{code_base}" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
""",
            encoding="utf-8",
        )


if __name__ == "__main__":
    unittest.main()
