#!/usr/bin/env python3

import argparse
import json
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET


MAX_REPORT_BYTES = 100 * 1024 * 1024
MAX_TOTAL_BYTES = 1024 * 1024 * 1024
SUITES = ("BVT", "SlowBVT", "Functional")
DETERMINISTIC_SOURCE_PREFIX = "/_/src/"


def parse_arguments():
    parser = argparse.ArgumentParser(
        description="Combine MTP Cobertura reports into repository line coverage."
    )
    parser.add_argument("report_directory", type=Path)
    parser.add_argument("--json-output", required=True, type=Path)
    parser.add_argument("--markdown-output", required=True, type=Path)
    parser.add_argument("--source-root", required=True, type=Path)
    return parser.parse_args()


def get_repository_path(filename, source_root):
    normalized = filename.replace("\\", "/")
    normalized_source_root = str(source_root.resolve()).replace("\\", "/").rstrip("/")
    source_prefix = f"{normalized_source_root}/"
    if normalized.casefold().startswith(source_prefix.casefold()):
        relative_path = normalized[len(source_prefix) :]
    elif normalized.startswith(DETERMINISTIC_SOURCE_PREFIX):
        relative_path = normalized[len(DETERMINISTIC_SOURCE_PREFIX) :]
    else:
        return None

    path_parts = PurePosixPath(relative_path).parts
    is_build_output = any(
        part.casefold() in ("bin", "obj") for part in path_parts
    )
    if ".." in path_parts or is_build_output:
        return None

    return f"src/{relative_path}"


def read_report(coverage_report, source_root):
    if coverage_report.is_symlink():
        raise RuntimeError(f"{coverage_report} must not be a symbolic link")

    report_size = coverage_report.stat().st_size
    if report_size > MAX_REPORT_BYTES:
        raise RuntimeError(f"{coverage_report} exceeds the 100 MB parsing limit")

    try:
        report_text = coverage_report.read_bytes().decode("utf-8-sig")
    except UnicodeDecodeError:
        raise RuntimeError(f"{coverage_report} must contain valid UTF-8") from None

    uppercase_report = report_text.upper()
    if "<!DOCTYPE" in uppercase_report or "<!ENTITY" in uppercase_report:
        raise RuntimeError("Coverage report contains unsupported XML declarations")

    root = ET.fromstring(report_text)
    measured_lines = {}

    for class_element in root.iter("class"):
        filename = class_element.get("filename")
        if not filename:
            continue

        repository_path = get_repository_path(filename, source_root)
        if repository_path is None:
            continue

        lines_element = class_element.find("lines")
        if lines_element is None:
            continue

        for line_element in lines_element.findall("line"):
            try:
                line_number = int(line_element.get("number", ""))
                hits = int(line_element.get("hits", ""))
            except ValueError:
                continue

            line_key = (repository_path, line_number)
            measured_lines[line_key] = measured_lines.get(line_key, False) or hits > 0

    return measured_lines


def get_suite(coverage_report):
    for path_part in coverage_report.parts:
        if path_part.startswith("coverage_output_"):
            return path_part.removeprefix("coverage_output_")

    raise RuntimeError(f"{coverage_report} is not under a coverage artifact directory")


def read_coverage(report_directory, source_root):
    reports = sorted(report_directory.rglob("*.cobertura.xml"))
    if not reports:
        raise RuntimeError(f"No Cobertura reports found under {report_directory}")

    total_bytes = sum(report.stat().st_size for report in reports)
    if total_bytes > MAX_TOTAL_BYTES:
        raise RuntimeError("Coverage reports exceed the 1 GB parsing limit")

    suite_lines = {suite: {} for suite in SUITES}
    for report in reports:
        suite = get_suite(report)
        if suite not in suite_lines:
            raise RuntimeError(f"Unexpected coverage suite: {suite}")

        for line_key, covered in read_report(report, source_root).items():
            suite_lines[suite][line_key] = (
                suite_lines[suite].get(line_key, False) or covered
            )

    missing_suites = [suite for suite, lines in suite_lines.items() if not lines]
    if missing_suites:
        raise RuntimeError(
            f"Coverage reports are missing for: {', '.join(missing_suites)}"
        )

    return reports, suite_lines


def get_statistics(measured_lines):
    covered_lines = sum(measured_lines.values())
    total_lines = len(measured_lines)
    line_rate = covered_lines / total_lines
    return {
        "covered_lines": covered_lines,
        "line_rate": line_rate,
        "line_rate_display": f"{line_rate:.2%}",
        "total_lines": total_lines,
    }


def write_outputs(reports, suite_lines, json_output, markdown_output):
    combined_lines = {}
    for measured_lines in suite_lines.values():
        for line_key, covered in measured_lines.items():
            combined_lines[line_key] = combined_lines.get(line_key, False) or covered

    combined = get_statistics(combined_lines)
    suites = {suite: get_statistics(lines) for suite, lines in suite_lines.items()}
    source_files = len({path for path, _ in combined_lines})

    summary = {
        **combined,
        "reports": len(reports),
        "source_files": source_files,
        "suites": suites,
    }
    json_output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

    markdown_output.write_text(
        "\n".join(
            [
                "## Code coverage",
                "",
                "| Test scope | Line coverage | Covered lines |",
                "| --- | ---: | ---: |",
                f"| **Combined** | **{combined['line_rate_display']}** | "
                f"**{combined['covered_lines']:,} / {combined['total_lines']:,}** |",
                *[
                    f"| {suite} | {suites[suite]['line_rate_display']} | "
                    f"{suites[suite]['covered_lines']:,} / "
                    f"{suites[suite]['total_lines']:,} |"
                    for suite in SUITES
                ],
                "",
                f"Measured {source_files:,} source files across {len(reports):,} "
                "coverage reports.",
                "",
                "Coverage combines the BVT, SlowBVT, and Functional suites on "
                "Linux with .NET 10 and measures repository source under `src/`.",
                "",
            ]
        ),
        encoding="utf-8",
    )


def main():
    arguments = parse_arguments()
    reports, suite_lines = read_coverage(
        arguments.report_directory, arguments.source_root
    )
    write_outputs(
        reports,
        suite_lines,
        arguments.json_output,
        arguments.markdown_output,
    )


if __name__ == "__main__":
    main()
