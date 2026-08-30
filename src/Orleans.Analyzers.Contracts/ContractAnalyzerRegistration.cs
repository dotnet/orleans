using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GrainInterfaceVersionAnalyzerRegistration : DiagnosticAnalyzer
{
    private readonly GrainInterfaceVersionAnalyzer _inner = new();

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => _inner.SupportedDiagnostics;

    public override void Initialize(AnalysisContext context) => _inner.Initialize(context);
}

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GrainInterfaceVersionCodeFix)), Shared]
public sealed class GrainInterfaceVersionCodeFixRegistration : CodeFixProvider
{
    private readonly GrainInterfaceVersionCodeFix _inner = new();

    public override ImmutableArray<string> FixableDiagnosticIds => _inner.FixableDiagnosticIds;

    public override FixAllProvider GetFixAllProvider() => _inner.GetFixAllProvider();

    public override Task RegisterCodeFixesAsync(CodeFixContext context) => _inner.RegisterCodeFixesAsync(context);
}
