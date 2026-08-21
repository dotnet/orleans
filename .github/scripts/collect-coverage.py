#!/usr/bin/env python3

import argparse
from pathlib import Path, PurePosixPath
import subprocess
import xml.etree.ElementTree as ET


MAX_TRX_BYTES = 100 * 1024 * 1024
TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def parse_arguments():
    parser = argparse.ArgumentParser(
        description="Collect MTP coverage for test modules selected by a TRX run."
    )
    parser.add_argument("--repository-root", required=True, type=Path)
    parser.add_argument("--results-directory", required=True, type=Path)
    parser.add_argument(
        "--suite", required=True, choices=("BVT", "SlowBVT", "Functional")
    )
    return parser.parse_args()


def read_trx(trx_path):
    if trx_path.is_symlink():
        raise RuntimeError(f"{trx_path} must not be a symbolic link")

    if trx_path.stat().st_size > MAX_TRX_BYTES:
        raise RuntimeError(f"{trx_path} exceeds the 100 MB parsing limit")

    try:
        trx_text = trx_path.read_bytes().decode("utf-8-sig")
    except UnicodeDecodeError:
        raise RuntimeError(f"{trx_path} must contain valid UTF-8") from None

    uppercase_trx = trx_text.upper()
    if "<!DOCTYPE" in uppercase_trx or "<!ENTITY" in uppercase_trx:
        raise RuntimeError(f"{trx_path} contains unsupported XML declarations")

    return ET.fromstring(trx_text)


def get_selected_modules(results_directory, repository_root):
    repository_root = repository_root.resolve()
    test_root = (repository_root / "test").resolve()
    modules = set()

    trx_files = sorted(results_directory.rglob("*.trx"))
    if not trx_files:
        raise RuntimeError(f"No TRX reports found under {results_directory}")

    namespace = {"trx": TRX_NAMESPACE}
    for trx_path in trx_files:
        root = read_trx(trx_path)
        counters = root.find("trx:ResultSummary/trx:Counters", namespace)
        if counters is None or int(counters.get("total", "0")) == 0:
            continue

        test_method = root.find(
            "trx:TestDefinitions/trx:UnitTest/trx:TestMethod", namespace
        )
        if test_method is None or not test_method.get("codeBase"):
            raise RuntimeError(f"{trx_path} does not identify its test module")

        code_base = test_method.get("codeBase").replace("\\", "/")
        marker = "/test/"
        marker_index = code_base.casefold().find(marker)
        if marker_index < 0:
            raise RuntimeError(f"{trx_path} references a module outside test/")

        relative_module = PurePosixPath(code_base[marker_index + 1 :])
        module_path = repository_root.joinpath(*relative_module.parts)
        if module_path.is_symlink():
            raise RuntimeError(f"{module_path} must not be a symbolic link")

        resolved_module = module_path.resolve()
        if not resolved_module.is_relative_to(test_root):
            raise RuntimeError(f"{trx_path} references a module outside test/")
        if module_path.suffix.casefold() != ".dll" or not module_path.is_file():
            raise RuntimeError(f"Test module does not exist: {module_path}")

        test_config = module_path.with_suffix(".testconfig.json")
        if not test_config.is_file() or test_config.is_symlink():
            raise RuntimeError(f"Test module has no valid configuration: {module_path}")

        modules.add(resolved_module)

    if not modules:
        raise RuntimeError("TRX reports contain no selected test modules")

    return sorted(modules)


def collect_coverage(repository_root, results_directory, suite):
    modules = get_selected_modules(results_directory, repository_root)
    coverage_directory = repository_root / "TestResults" / f"coverage-{suite}"
    coverage_directory.mkdir(parents=True, exist_ok=True)
    filter_query = f"/[(Provider=None)&(Suite={suite})&(Area!=CodeGen)]"
    settings_path = repository_root / ".github" / "coverage.config.xml"

    for index, module_path in enumerate(modules, start=1):
        output_path = coverage_directory / (
            f"{index:03d}-{module_path.stem}.cobertura.xml"
        )
        subprocess.run(
            [
                "dotnet",
                "exec",
                str(module_path),
                "--filter-query",
                filter_query,
                "--minimum-expected-tests",
                "1",
                "--hangdump",
                "--hangdump-timeout",
                "10m",
                "--crashdump",
                "--crashdump-type",
                "Full",
                "--hangdump-type",
                "Full",
                "--coverage",
                "--coverage-output",
                str(output_path),
                "--coverage-output-format",
                "cobertura",
                "--coverage-settings",
                str(settings_path),
            ],
            check=True,
            cwd=repository_root,
        )


def main():
    arguments = parse_arguments()
    collect_coverage(
        arguments.repository_root.resolve(),
        arguments.results_directory.resolve(),
        arguments.suite,
    )


if __name__ == "__main__":
    main()
