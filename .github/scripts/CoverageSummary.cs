#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Orleans.Coverage;

public sealed class CoverageSummaryResult
{
    public required long CoveredBranches { get; init; }

    public required long CoveredLines { get; init; }

    public required int Reports { get; init; }

    public required int SourceFiles { get; init; }

    public required long TotalBranches { get; init; }

    public required long TotalLines { get; init; }
}

public static class CoverageSummaryReader
{
    private const long MaximumReportBytes = 100 * 1024 * 1024;
    private const long MaximumSourceBytes = 10 * 1024 * 1024;
    private const int MaximumBranchesPerLine = 1024;
    private const string DeterministicSourcePrefix = "/_/src/";
    private static readonly Regex ConditionCoverage = new(
        @"^\s*(\d+(?:\.\d+)?)%\s+\((\d+)\s*/\s*(\d+)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CoverageSummaryResult Analyze(string reportDirectory, string sourceRoot)
    {
        var resolvedReportDirectory = Path.GetFullPath(reportDirectory);
        var resolvedSourceRoot = Path.GetFullPath(sourceRoot);
        AssertNotReparsePoint(resolvedReportDirectory);
        AssertNotReparsePoint(resolvedSourceRoot);

        var reports = Directory
            .EnumerateFiles(resolvedReportDirectory, "*.cobertura.xml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reports.Length == 0)
        {
            throw new InvalidDataException($"No Cobertura reports found under {resolvedReportDirectory}");
        }
        if (reports.Length > 1000)
        {
            throw new InvalidDataException($"Expected at most 1000 Cobertura reports, found {reports.Length}");
        }

        var measuredFiles = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
        var measuredBranches = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        var sourceLineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var normalizedSourceRoot = resolvedSourceRoot.Replace('\\', '/').TrimEnd('/');
        foreach (var report in reports)
        {
            var measuredLineEntries = ReadCoverageReport(
                report,
                resolvedSourceRoot,
                normalizedSourceRoot,
                sourceLineCounts,
                measuredFiles,
                measuredBranches);
            if (measuredLineEntries == 0)
            {
                throw new InvalidDataException($"{report} contains no measured lines under the source root");
            }
        }

        long totalLines = 0;
        long coveredLines = 0;
        foreach (var fileLines in measuredFiles.Values)
        {
            totalLines += fileLines.Count;
            coveredLines += fileLines.Values.Count(static covered => covered);
        }
        if (totalLines == 0)
        {
            throw new InvalidDataException("Merged coverage report contains no measured lines under the source root");
        }

        long totalBranches = 0;
        long coveredBranches = 0;
        foreach (var fileBranches in measuredBranches.Values)
        {
            totalBranches += fileBranches.Count;
            coveredBranches += fileBranches.Values.Count(static covered => covered);
        }

        return new CoverageSummaryResult
        {
            CoveredBranches = coveredBranches,
            CoveredLines = coveredLines,
            Reports = reports.Length,
            SourceFiles = measuredFiles.Count,
            TotalBranches = totalBranches,
            TotalLines = totalLines,
        };
    }

    private static int ReadCoverageReport(
        string reportPath,
        string resolvedSourceRoot,
        string normalizedSourceRoot,
        Dictionary<string, int> sourceLineCounts,
        Dictionary<string, Dictionary<int, bool>> measuredFiles,
        Dictionary<string, Dictionary<string, bool>> measuredBranches)
    {
        AssertNotReparsePoint(reportPath);
        var report = new FileInfo(reportPath);
        if (report.Length > MaximumReportBytes)
        {
            throw new InvalidDataException($"{reportPath} exceeds the 100 MB parsing limit");
        }

        string reportText;
        try
        {
            reportText = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(File.ReadAllBytes(reportPath));
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException($"{reportPath} must contain valid UTF-8");
        }
        if (reportText.Length > 0 && reportText[0] == '\ufeff')
        {
            reportText = reportText[1..];
        }
        if (reportText.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || reportText.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Coverage report contains unsupported XML declarations");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumReportBytes,
        };
        using var stringReader = new StringReader(reportText);
        using var reader = XmlReader.Create(stringReader, settings);
        var classDepth = -1;
        var methodDepth = -1;
        var classLinesDepth = -1;
        var measuredLineEntries = 0;
        RepositorySourcePath? sourcePath = null;
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "class")
                    {
                        classDepth = reader.Depth;
                        methodDepth = -1;
                        classLinesDepth = -1;
                        sourcePath = GetRepositoryPath(reader.GetAttribute("filename"), normalizedSourceRoot);
                        if (reader.IsEmptyElement)
                        {
                            classDepth = -1;
                            sourcePath = null;
                        }

                        continue;
                    }
                    if (classDepth < 0)
                    {
                        continue;
                    }
                    if (reader.LocalName == "method")
                    {
                        if (!reader.IsEmptyElement)
                        {
                            methodDepth = reader.Depth;
                        }

                        continue;
                    }
                    if (reader.LocalName == "lines" && methodDepth < 0 && reader.Depth == classDepth + 1)
                    {
                        if (!reader.IsEmptyElement)
                        {
                            classLinesDepth = reader.Depth;
                        }

                        continue;
                    }
                    if (reader.LocalName != "line"
                        || classLinesDepth < 0
                        || reader.Depth != classLinesDepth + 1
                        || sourcePath is null)
                    {
                        continue;
                    }

                    ProcessLine(
                        reader,
                        reportPath,
                        resolvedSourceRoot,
                        sourcePath,
                        sourceLineCounts,
                        measuredFiles,
                        measuredBranches);
                    measuredLineEntries++;
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == classLinesDepth && reader.LocalName == "lines")
                    {
                        classLinesDepth = -1;
                    }
                    else if (reader.Depth == methodDepth && reader.LocalName == "method")
                    {
                        methodDepth = -1;
                    }
                    else if (reader.Depth == classDepth && reader.LocalName == "class")
                    {
                        classDepth = -1;
                        methodDepth = -1;
                        classLinesDepth = -1;
                        sourcePath = null;
                    }
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"{reportPath} contains invalid XML: {exception.Message}", exception);
        }

        return measuredLineEntries;
    }

    private static void ProcessLine(
        XmlReader reader,
        string reportPath,
        string resolvedSourceRoot,
        RepositorySourcePath sourcePath,
        Dictionary<string, int> sourceLineCounts,
        Dictionary<string, Dictionary<int, bool>> measuredFiles,
        Dictionary<string, Dictionary<string, bool>> measuredBranches)
    {
        if (!int.TryParse(reader.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber)
            || !int.TryParse(reader.GetAttribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits)
            || lineNumber <= 0
            || hits < 0)
        {
            throw new InvalidDataException($"{reportPath} contains invalid line coverage for {sourcePath.RepositoryPath}");
        }

        if (!sourceLineCounts.TryGetValue(sourcePath.RepositoryPath, out var sourceLineCount))
        {
            sourceLineCount = GetSourceLineCount(resolvedSourceRoot, sourcePath);
            sourceLineCounts.Add(sourcePath.RepositoryPath, sourceLineCount);
        }
        if (lineNumber > sourceLineCount)
        {
            throw new InvalidDataException($"{reportPath} references line {lineNumber} beyond the end of {sourcePath.RepositoryPath}");
        }

        if (!measuredFiles.TryGetValue(sourcePath.RepositoryPath, out var fileLines))
        {
            fileLines = [];
            measuredFiles.Add(sourcePath.RepositoryPath, fileLines);
        }
        fileLines.TryGetValue(lineNumber, out var covered);
        fileLines[lineNumber] = covered || hits > 0;

        if (!string.Equals(reader.GetAttribute("branch"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var conditionCoverage = reader.GetAttribute("condition-coverage") ?? string.Empty;
        var match = ConditionCoverage.Match(conditionCoverage);
        if (!match.Success)
        {
            throw new InvalidDataException($"Branch line {lineNumber} has invalid condition coverage '{conditionCoverage}'");
        }
        var coveragePercentText = match.Groups[1].Value;
        var coveragePercent = decimal.Parse(coveragePercentText, CultureInfo.InvariantCulture);
        var coveredBranches = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var totalBranches = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (totalBranches <= 0 || totalBranches > MaximumBranchesPerLine || coveredBranches > totalBranches)
        {
            throw new InvalidDataException($"Branch line {lineNumber} has inconsistent condition coverage '{conditionCoverage}'");
        }

        var decimalSeparator = coveragePercentText.IndexOf('.');
        var decimalPlaces = decimalSeparator >= 0 ? coveragePercentText.Length - decimalSeparator - 1 : 0;
        var roundingUnit = 1m;
        for (var digit = 0; digit < decimalPlaces; digit++)
        {
            roundingUnit /= 10;
        }
        var expectedCoveragePercent = 100m * coveredBranches / totalBranches;
        if (Math.Abs(coveragePercent - expectedCoveragePercent) > roundingUnit / 2)
        {
            throw new InvalidDataException($"Branch line {lineNumber} has inconsistent condition coverage '{conditionCoverage}'");
        }

        if (!measuredBranches.TryGetValue(sourcePath.RepositoryPath, out var fileBranches))
        {
            fileBranches = new Dictionary<string, bool>(StringComparer.Ordinal);
            measuredBranches.Add(sourcePath.RepositoryPath, fileBranches);
        }
        for (var branch = 0; branch < totalBranches; branch++)
        {
            var branchKey = $"{lineNumber}\0aggregate\0{branch}";
            fileBranches.TryGetValue(branchKey, out var branchCovered);
            fileBranches[branchKey] = branchCovered || branch < coveredBranches;
        }
    }

    private static RepositorySourcePath? GetRepositoryPath(string? filename, string normalizedSourceRoot)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return null;
        }

        var normalized = filename.Replace('\\', '/');
        var sourcePrefix = $"{normalizedSourceRoot}/";
        string relativePath;
        if (normalized.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalized[sourcePrefix.Length..];
        }
        else if (normalized.StartsWith(DeterministicSourcePrefix, StringComparison.Ordinal))
        {
            relativePath = normalized[DeterministicSourcePrefix.Length..];
        }
        else
        {
            return null;
        }

        var pathParts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length == 0
            || relativePath != string.Join('/', pathParts)
            || pathParts.Any(static part => part is "." or ".."))
        {
            throw new InvalidDataException($"Coverage report contains invalid repository source path '{filename}'");
        }
        if (pathParts.Any(static part => part is "bin" or "obj"))
        {
            return null;
        }

        return new RepositorySourcePath(relativePath, $"src/{relativePath}");
    }

    private static int GetSourceLineCount(string resolvedSourceRoot, RepositorySourcePath sourcePath)
    {
        var currentPath = resolvedSourceRoot;
        AssertNotReparsePoint(currentPath);
        foreach (var part in sourcePath.RelativePath.Split('/'))
        {
            currentPath = Path.Combine(currentPath, part);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                throw new InvalidDataException($"Coverage report references missing source file {sourcePath.RepositoryPath}");
            }

            AssertNotReparsePoint(currentPath);
        }

        var sourceFile = new FileInfo(currentPath);
        if (!sourceFile.Exists)
        {
            throw new InvalidDataException($"Coverage report source path {sourcePath.RepositoryPath} is not a file");
        }
        if (sourceFile.Length > MaximumSourceBytes)
        {
            throw new InvalidDataException($"{currentPath} exceeds the 10 MB source validation limit");
        }

        var lineCount = 0;
        using var reader = sourceFile.OpenText();
        while (reader.ReadLine() is not null)
        {
            lineCount++;
        }

        return lineCount;
    }

    private static void AssertNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{path} must not be a symbolic link");
        }
    }

    private sealed record RepositorySourcePath(string RelativePath, string RepositoryPath);
}
