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
    public long CoveredBranches { get; set; }

    public long CoveredLines { get; set; }

    public int Reports { get; set; }

    public int SourceFiles { get; set; }

    public long TotalBranches { get; set; }

    public long TotalLines { get; set; }
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
        var measuredBranchTotals = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
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
                measuredBranches,
                measuredBranchTotals);
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
        Dictionary<string, Dictionary<string, bool>> measuredBranches,
        Dictionary<string, Dictionary<string, int>> measuredBranchTotals)
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
        var linesDepth = -1;
        var measuredLineEntries = 0;
        var classIdentity = string.Empty;
        var methodIdentity = string.Empty;
        var linesBelongToMethod = false;
        var methodBranchLines = new HashSet<int>();
        var pendingClassBranches = new List<PendingBranchLine>();
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
                        linesDepth = -1;
                        classIdentity = reader.GetAttribute("name") ?? string.Empty;
                        methodIdentity = string.Empty;
                        linesBelongToMethod = false;
                        methodBranchLines.Clear();
                        pendingClassBranches.Clear();
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
                            methodIdentity = $"{reader.GetAttribute("name")}\0{reader.GetAttribute("signature")}";
                        }

                        continue;
                    }
                    if (reader.LocalName == "lines"
                        && ((methodDepth >= 0 && reader.Depth == methodDepth + 1)
                            || (methodDepth < 0 && reader.Depth == classDepth + 1)))
                    {
                        if (!reader.IsEmptyElement)
                        {
                            linesDepth = reader.Depth;
                            linesBelongToMethod = methodDepth >= 0;
                        }

                        continue;
                    }
                    if (reader.LocalName != "line"
                        || linesDepth < 0
                        || reader.Depth != linesDepth + 1
                        || sourcePath is null)
                    {
                        continue;
                    }

                    if (!int.TryParse(
                        reader.GetAttribute("number"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var lineNumber)
                        || lineNumber <= 0)
                    {
                        throw new InvalidDataException(
                            $"{reportPath} contains invalid line coverage for {sourcePath.RepositoryPath}");
                    }
                    var branchSiteIdentity = string.Empty;
                    if (string.Equals(reader.GetAttribute("branch"), "true", StringComparison.OrdinalIgnoreCase))
                    {
                        if (linesBelongToMethod)
                        {
                            methodBranchLines.Add(lineNumber);
                            branchSiteIdentity = $"{classIdentity}\0{methodIdentity}";
                        }
                        else if (!methodBranchLines.Contains(lineNumber))
                        {
                            pendingClassBranches.Add(
                                new PendingBranchLine(
                                    lineNumber,
                                    reader.GetAttribute("condition-coverage") ?? string.Empty));
                        }
                    }

                    ProcessLine(
                        reader,
                        lineNumber,
                        branchSiteIdentity,
                        reportPath,
                        resolvedSourceRoot,
                        sourcePath,
                        sourceLineCounts,
                        measuredFiles,
                        measuredBranches,
                        measuredBranchTotals);
                    measuredLineEntries++;
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == linesDepth && reader.LocalName == "lines")
                    {
                        linesDepth = -1;
                        linesBelongToMethod = false;
                    }
                    else if (reader.Depth == methodDepth && reader.LocalName == "method")
                    {
                        methodDepth = -1;
                        methodIdentity = string.Empty;
                    }
                    else if (reader.Depth == classDepth && reader.LocalName == "class")
                    {
                        if (sourcePath is not null)
                        {
                            foreach (var pendingBranch in pendingClassBranches)
                            {
                                if (!methodBranchLines.Contains(pendingBranch.LineNumber))
                                {
                                    ProcessBranch(
                                        pendingBranch.ConditionCoverage,
                                        pendingBranch.LineNumber,
                                        $"{classIdentity}\0<non-method>",
                                        reportPath,
                                        sourcePath,
                                        measuredBranches,
                                        measuredBranchTotals);
                                }
                            }
                        }

                        classDepth = -1;
                        methodDepth = -1;
                        linesDepth = -1;
                        classIdentity = string.Empty;
                        methodIdentity = string.Empty;
                        linesBelongToMethod = false;
                        methodBranchLines.Clear();
                        pendingClassBranches.Clear();
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
        int lineNumber,
        string branchSiteIdentity,
        string reportPath,
        string resolvedSourceRoot,
        RepositorySourcePath sourcePath,
        Dictionary<string, int> sourceLineCounts,
        Dictionary<string, Dictionary<int, bool>> measuredFiles,
        Dictionary<string, Dictionary<string, bool>> measuredBranches,
        Dictionary<string, Dictionary<string, int>> measuredBranchTotals)
    {
        if (!int.TryParse(reader.GetAttribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits)
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
            fileLines = new Dictionary<int, bool>();
            measuredFiles.Add(sourcePath.RepositoryPath, fileLines);
        }
        fileLines.TryGetValue(lineNumber, out var covered);
        fileLines[lineNumber] = covered || hits > 0;

        if (branchSiteIdentity.Length == 0)
        {
            return;
        }

        ProcessBranch(
            reader.GetAttribute("condition-coverage") ?? string.Empty,
            lineNumber,
            branchSiteIdentity,
            reportPath,
            sourcePath,
            measuredBranches,
            measuredBranchTotals);
    }

    private static void ProcessBranch(
        string conditionCoverage,
        int lineNumber,
        string branchSiteIdentity,
        string reportPath,
        RepositorySourcePath sourcePath,
        Dictionary<string, Dictionary<string, bool>> measuredBranches,
        Dictionary<string, Dictionary<string, int>> measuredBranchTotals)
    {
        var match = ConditionCoverage.Match(conditionCoverage);
        if (!match.Success)
        {
            throw new InvalidDataException(
                $"{reportPath} contains invalid condition coverage '{conditionCoverage}' for {sourcePath.RepositoryPath}:{lineNumber}");
        }
        var coveragePercentText = match.Groups[1].Value;
        var coveragePercent = decimal.Parse(coveragePercentText, CultureInfo.InvariantCulture);
        var coveredBranches = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var totalBranches = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (totalBranches <= 0 || totalBranches > MaximumBranchesPerLine || coveredBranches > totalBranches)
        {
            throw new InvalidDataException(
                $"{reportPath} contains inconsistent condition coverage '{conditionCoverage}' for {sourcePath.RepositoryPath}:{lineNumber}");
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
            throw new InvalidDataException(
                $"{reportPath} contains inconsistent condition coverage '{conditionCoverage}' for {sourcePath.RepositoryPath}:{lineNumber}");
        }

        if (!measuredBranchTotals.TryGetValue(sourcePath.RepositoryPath, out var fileBranchTotals))
        {
            fileBranchTotals = new Dictionary<string, int>(StringComparer.Ordinal);
            measuredBranchTotals.Add(sourcePath.RepositoryPath, fileBranchTotals);
        }
        var branchSiteKey = $"{branchSiteIdentity}\0{lineNumber}";
        if (fileBranchTotals.TryGetValue(branchSiteKey, out var existingTotalBranches)
            && existingTotalBranches != totalBranches)
        {
            var displayBranchSite = branchSiteIdentity.Replace('\0', '/');
            throw new InvalidDataException(
                $"{reportPath} reports {totalBranches} branches for {sourcePath.RepositoryPath}:{lineNumber} at {displayBranchSite}, expected {existingTotalBranches}");
        }
        fileBranchTotals[branchSiteKey] = totalBranches;

        if (!measuredBranches.TryGetValue(sourcePath.RepositoryPath, out var fileBranches))
        {
            fileBranches = new Dictionary<string, bool>(StringComparer.Ordinal);
            measuredBranches.Add(sourcePath.RepositoryPath, fileBranches);
        }
        for (var branch = 0; branch < totalBranches; branch++)
        {
            var branchKey = $"{branchSiteKey}\0aggregate\0{branch}";
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

    private sealed record PendingBranchLine(int LineNumber, string ConditionCoverage);

    private sealed record RepositorySourcePath(string RelativePath, string RepositoryPath);
}
