using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.DurableTasks.Analyzers;
using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>
/// Runs the durable task analyzers over source which marks each expected diagnostic as <c>{|ORLEANS0100:text|}</c>, and
/// verifies code fixes for them.
/// </summary>
/// <remarks>
/// The source compiles against <see cref="DurableTaskStubs"/>, which declares the durable task programming model by the
/// metadata names which the analyzers match, so the tests don't depend on the abstractions assembly's version.
/// </remarks>
internal static partial class AnalyzerVerifier
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Logging;
        using Orleans.DurableTasks;
        using Orleans.Journaling;

        """;

    /// <summary>The framework and logging assemblies which test sources compile against.</summary>
    internal static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(static path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "mscorlib.dll" or "netstandard.dll")
            .Select(static path => MetadataReference.CreateFromFile(path)),
        MetadataReference.CreateFromFile(typeof(ILogger).Assembly.Location),
    ];

    private static readonly ImmutableArray<DiagnosticAnalyzer> Analyzers =
    [
        new DurableTaskReplayAnalyzer(),
        new DurableTaskDefinitionAnalyzer(),
        new DurableTaskCleanupAnalyzer(),
        new DurableTaskVersionAnalyzer(),
        new DurableTaskIdentityAnalyzer(),
        new DurableTaskDelegateAnalyzer(),
        new DurableTaskStateMutationAnalyzer(),
    ];

    /// <summary>Verifies that the analyzers report exactly the marked diagnostics in <paramref name="source"/>.</summary>
    public static async Task VerifyAsync(string source)
    {
        var (text, expected) = ParseMarkers(Usings + source);
        var diagnostics = await GetDiagnosticsAsync(CreateProject(text));
        var actual = diagnostics
            .Where(static diagnostic => diagnostic.Location.SourceTree?.FilePath == "Test.cs")
            .Select(diagnostic => (diagnostic.Id, Text: text.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length), diagnostic.Location.SourceSpan.Start))
            .OrderBy(static diagnostic => diagnostic.Start)
            .ToArray();

        Assert.True(
            expected.SequenceEqual(actual),
            $"Expected:{Environment.NewLine}{Format(expected)}{Environment.NewLine}Actual:{Environment.NewLine}{Format(actual)}{Environment.NewLine}"
            + string.Join(Environment.NewLine, diagnostics.Select(static d => d.ToString())));
    }

    /// <summary>
    /// Verifies the marked diagnostics in <paramref name="source"/>, fixes all of those which <paramref name="fixer"/> fixes
    /// in the document, and verifies that the result is <paramref name="fixedSource"/>, which has only its marked diagnostics.
    /// </summary>
    public static async Task VerifyCodeFixAsync(CodeFixProvider fixer, string source, string fixedSource)
    {
        await VerifyAsync(source);
        var project = CreateProject(ParseMarkers(Usings + source).Text);
        var document = project.Documents.Single(static document => document.Name == "Test.cs");
        var diagnostics = (await GetDiagnosticsAsync(project))
            .Where(diagnostic => fixer.FixableDiagnosticIds.Contains(diagnostic.Id))
            .ToImmutableArray();
        Assert.NotEmpty(diagnostics);

        var actions = new List<CodeAction>();
        await fixer.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostics[0], (action, _) => actions.Add(action), TestContext.Current.CancellationToken));
        var action = Assert.Single(actions);
        var fixAll = await fixer.GetFixAllProvider()!.GetFixAsync(new FixAllContext(
            document,
            fixer,
            FixAllScope.Document,
            action.EquivalenceKey,
            fixer.FixableDiagnosticIds,
            new FixedDiagnosticProvider(diagnostics),
            TestContext.Current.CancellationToken));
        Assert.NotNull(fixAll);
        var operations = await fixAll.GetOperationsAsync(TestContext.Current.CancellationToken);
        var changedDocument = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetDocument(document.Id)!;
        Assert.Equal(Usings + ParseMarkers(fixedSource).Text, (await changedDocument.GetTextAsync(TestContext.Current.CancellationToken)).ToString());
        await VerifyAsync(fixedSource);
    }

    private static Project CreateProject(string text)
    {
        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                "Test",
                "Test",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
                metadataReferences: References))
            .AddDocument(DocumentId.CreateNewId(projectId), "Test.cs", text, filePath: "Test.cs")
            .AddDocument(DocumentId.CreateNewId(projectId), "Stubs.cs", DurableTaskStubs.Source, filePath: "Stubs.cs");
        return solution.GetProject(projectId)!;
    }

    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(Project project)
    {
        var compilation = (await project.GetCompilationAsync(TestContext.Current.CancellationToken))!;
        var errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, "The test source doesn't compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));

        var exceptions = new List<Exception>();
        var options = new CompilationWithAnalyzersOptions(
            new AnalyzerOptions([]),
            onAnalyzerException: (exception, _, _) => exceptions.Add(exception),
            concurrentAnalysis: false,
            logAnalyzerExecutionTime: false);
        var diagnostics = await compilation.WithAnalyzers(Analyzers, options).GetAnalyzerDiagnosticsAsync();
        Assert.True(exceptions.Count == 0, "An analyzer threw:" + Environment.NewLine + string.Join(Environment.NewLine, exceptions));
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "AD0001");
        return diagnostics;
    }

    private static string Format((string Id, string Text, int Start)[] diagnostics) =>
        diagnostics.Length == 0 ? "  (none)" : string.Join(Environment.NewLine, diagnostics.Select(static d => $"  {d.Id} at {d.Start}: {d.Text}"));

    private static (string Text, (string Id, string Text, int Start)[] Expected) ParseMarkers(string source)
    {
        var text = new StringBuilder();
        var expected = new List<(string Id, string Text, int Start)>();
        var position = 0;
        foreach (Match match in MarkerPattern().Matches(source))
        {
            text.Append(source, position, match.Index - position);
            expected.Add((match.Groups["id"].Value, match.Groups["text"].Value, text.Length));
            text.Append(match.Groups["text"].Value);
            position = match.Index + match.Length;
        }

        text.Append(source, position, source.Length - position);
        return (text.ToString(), expected.OrderBy(static e => e.Start).ToArray());
    }

    [GeneratedRegex(@"\{\|(?<id>ORLEANS\d{4}):(?<text>.*?)\|\}", RegexOptions.Singleline)]
    private static partial Regex MarkerPattern();

    /// <summary>Supplies the diagnostics which a fix-all operation fixes.</summary>
    private sealed class FixedDiagnosticProvider(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
    }
}
