#!/usr/bin/env python3

import argparse
import json
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET


MAX_REPORT_BYTES = 100 * 1024 * 1024
DETERMINISTIC_SOURCE_PREFIX = "/_/src/"


def parse_arguments():
    parser = argparse.ArgumentParser(
        description="Summarize merged MTP Cobertura repository line coverage."
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


def read_coverage(report_directory, source_root):
    reports = sorted(report_directory.rglob("*.cobertura.xml"))
    if not reports:
        raise RuntimeError(f"No Cobertura reports found under {report_directory}")
    if len(reports) != 1:
        raise RuntimeError(
            f"Expected one merged Cobertura report, found {len(reports)}"
        )

    measured_lines = read_report(reports[0], source_root)
    if not measured_lines:
        raise RuntimeError(
            "Merged coverage report contains no measured lines under the source root"
        )

    return measured_lines


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


def write_outputs(measured_lines, json_output, markdown_output):
    combined = get_statistics(measured_lines)
    source_files = len({path for path, _ in measured_lines})

    summary = {
        **combined,
        "reports": 1,
        "source_files": source_files,
    }
    json_output.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

    markdown_output.write_text(
        "\n".join(
            [
                "## Code coverage",
                "",
                "| Line coverage | Covered lines |",
                "| ---: | ---: |",
                f"| **{combined['line_rate_display']}** | "
                f"**{combined['covered_lines']:,} / {combined['total_lines']:,}** |",
                "",
                f"Measured {source_files:,} source files from the merged coverage "
                "report.",
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
    measured_lines = read_coverage(
        arguments.report_directory, arguments.source_root
    )
    write_outputs(
        measured_lines,
        arguments.json_output,
        arguments.markdown_output,
    )


if __name__ == "__main__":
    main()
