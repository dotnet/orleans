using Microsoft.CodeAnalysis.Diagnostics;
using Orleans.Analyzers;
using Xunit;

namespace Analyzers.Tests;

[TestCategory("BVT"), TestCategory("Analyzer")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Analyzer")]
public class DiagnosticHelpLinkTests
{
    [Fact]
    public void AllAnalyzerDiagnosticsUseStableHelpLinks()
    {
        var analyzerType = typeof(AlwaysInterleaveDiagnosticAnalyzer);
        var analyzers = analyzerType.Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(DiagnosticAnalyzer)))
            .Select(type => Assert.IsAssignableFrom<DiagnosticAnalyzer>(Activator.CreateInstance(type)))
            .ToArray();

        Assert.NotEmpty(analyzers);
        foreach (var analyzer in analyzers)
        {
            foreach (var diagnostic in analyzer.SupportedDiagnostics)
            {
                Assert.Equal(
                    $"https://aka.ms/orleans/diagnostics#{diagnostic.Id.ToLowerInvariant()}",
                    diagnostic.HelpLinkUri);
            }
        }
    }
}
