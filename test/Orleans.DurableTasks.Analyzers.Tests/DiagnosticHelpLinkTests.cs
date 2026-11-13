using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests that each durable task diagnostic links to a documented index entry and page with its severity.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DiagnosticHelpLinkTests
{
    [Fact]
    public void DiagnosticsLinkToDocumentedIndexEntriesAndPages()
    {
        var docs = FindDiagnosticsDocs();
        var index = File.ReadAllText(Path.Combine(docs, "index.md"));
        var diagnostics = typeof(DurableTaskReplayAnalyzer).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && type.IsAssignableTo(typeof(DiagnosticAnalyzer)))
            .Select(static type => Assert.IsAssignableFrom<DiagnosticAnalyzer>(Activator.CreateInstance(type)))
            .SelectMany(static analyzer => analyzer.SupportedDiagnostics)
            .DistinctBy(static diagnostic => diagnostic.Id)
            .ToArray();

        Assert.NotEmpty(diagnostics);
        foreach (var diagnostic in diagnostics)
        {
            var id = diagnostic.Id.ToLowerInvariant();
            var severity = diagnostic.DefaultSeverity.ToString();
            Assert.Equal($"https://aka.ms/orleans/diagnostics#{id}", diagnostic.HelpLinkUri);
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Description.ToString()), $"{diagnostic.Id} has no description.");

            // The help link's anchor is the index heading, whose entry links to the page and states the severity.
            var entry = Regex.Match(index, $@"^## {diagnostic.Id}\r?\n\r?\n\[[^\]]+\]\({id}\.md\) — (?<severity>\w+)\.", RegexOptions.Multiline);
            Assert.True(entry.Success, $"The diagnostics index has no entry for {diagnostic.Id} which links to {id}.md.");
            Assert.Equal(severity, entry.Groups["severity"].Value);

            var page = Path.Combine(docs, $"{id}.md");
            Assert.True(File.Exists(page), $"{diagnostic.Id} has no page.");
            Assert.Contains($"| Severity | {severity} |", File.ReadAllText(page), StringComparison.Ordinal);
        }
    }

    private static string FindDiagnosticsDocs()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var docs = Path.Combine(directory.FullName, "docs", "site", "src", "content", "docs", "diagnostics");
            if (File.Exists(Path.Combine(docs, "index.md")))
            {
                return docs;
            }
        }

        throw new DirectoryNotFoundException("The diagnostics documentation isn't in a directory which contains the test assembly.");
    }
}
