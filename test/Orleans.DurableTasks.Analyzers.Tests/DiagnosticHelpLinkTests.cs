using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests that each durable task diagnostic links to its entry in the Orleans diagnostics index.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DiagnosticHelpLinkTests
{
    [Fact]
    public void DiagnosticsLinkToTheirIndexAnchors()
    {
        var analyzers = typeof(DurableTaskReplayAnalyzer).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && type.IsAssignableTo(typeof(DiagnosticAnalyzer)))
            .Select(static type => Assert.IsAssignableFrom<DiagnosticAnalyzer>(Activator.CreateInstance(type)))
            .ToArray();

        var diagnostics = analyzers.SelectMany(static analyzer => analyzer.SupportedDiagnostics).ToArray();
        Assert.NotEmpty(diagnostics);
        foreach (var diagnostic in diagnostics)
        {
            Assert.Equal($"https://aka.ms/orleans/diagnostics#{diagnostic.Id.ToLowerInvariant()}", diagnostic.HelpLinkUri);
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Description.ToString()), $"{diagnostic.Id} has no description.");
        }
    }
}
