using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>
/// Tests for the suppressor which suppresses CS1998 for async functions returning Orleans durable tasks.
/// A durable task method builder defers the body, so a synchronous async durable task body is a normal durable step.
/// </summary>
/// <remarks>
/// C# compilers starting with Roslyn 5.0 no longer report CS1998, while earlier compilers which can load the Orleans analyzers do.
/// When the test's compiler does not report CS1998, <see cref="LegacyAsyncLacksAwaitAnalyzer"/> reports it at the locations
/// earlier compilers use, so the tests exercise the suppressor against the diagnostics it receives from them.
/// </remarks>
[TestCategory("BVT"), TestCategory("Analyzer")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Analyzer")]
public class DurableTaskAsyncMethodLacksAwaitSuppressorTests
{
    private const string Usings = """
        using System;
        using System.Threading.Tasks;
        using Orleans.DurableTasks;

        """;

    private static readonly bool CompilerReportsCS1998 = CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText("class C { async System.Threading.Tasks.Task M() { } }")],
            AnalyzerVerifier.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
        .GetDiagnostics()
        .Any(d => d.Id == DurableTaskAsyncMethodLacksAwaitSuppressor.SuppressedDiagnosticId);

    public static TheoryData<string> DurableTaskFunctions => new()
    {
        "public async DurableTask Run() { }",
        "public async DurableTask<int> Get() => 1;",
        "public static async DurableTask<T> Echo<T>(T value) => value;",
        "public void M() { async DurableTask Local() { } _ = Local(); }",
        "public void M() { async DurableTask<int> Local() => 1; _ = Local(); }",
        "public Func<DurableTask> M() => async () => { };",
        "public Func<DurableTask<int>> M() => async () => 1;",
        "public Func<int, DurableTask<int>> M() => async x => x;",
        "public Func<DurableTask> M() => async delegate { };",
        "public async DurableTask<int> Get() => DurableTask.RunInline(() => 1);",
        "public async DurableTask Run() { DurableTask.RunInline(() => { }); }",
    };

    public static TheoryData<string> OtherAsyncFunctions => new()
    {
        "public async Task Run() { }",
        "public async Task<int> Get() => 1;",
        "public async ValueTask Run() { }",
        "public async ValueTask<int> Get() => 1;",
        "public void M() { async Task<int> Local() => 1; _ = Local(); }",
        "public Func<Task<int>> M() => async () => 1;",
        "public Func<ValueTask> M() => async delegate { };",
    };

    [Theory]
    [MemberData(nameof(DurableTaskFunctions))]
    public async Task SuppressesCS1998ForDurableTaskFunctions(string member)
    {
        var diagnostics = await GetCS1998DiagnosticsAsync(Usings + $"public class C {{ {member} }}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.True(diagnostic.IsSuppressed);
    }

    [Theory]
    [MemberData(nameof(OtherAsyncFunctions))]
    public async Task DoesNotSuppressCS1998ForOtherTaskTypes(string member)
    {
        var diagnostics = await GetCS1998DiagnosticsAsync(Usings + $"public class C {{ {member} }}");

        var diagnostic = Assert.Single(diagnostics);
        Assert.False(diagnostic.IsSuppressed);
    }

    [Fact]
    public async Task DoesNotSuppressCS1998ForDurableTaskInOtherNamespace()
    {
        var source = CreateTaskLikeTypesSource("Contoso") + """
            public class C
            {
                public async Contoso.DurableTask Run() { }
                public async Contoso.DurableTask<int> Get() => 1;
                public System.Func<Contoso.DurableTask<int>> M() => async () => 1;
            }
            """;

        var diagnostics = await GetCS1998DiagnosticsAsync(source);

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.False(diagnostic.IsSuppressed));
    }

    [Fact]
    public async Task SuppressesOnlyTheDurableTaskFunctionWhenFunctionsAreNested()
    {
        var source = Usings + """
            public class C
            {
                public async DurableTask<int> DurableOuter()
                {
                    Func<Task<int>> inner = async () => 1;
                    return 1;
                }

                public async Task<int> TaskOuter()
                {
                    Func<DurableTask<int>> inner = async () => 1;
                    async Task<int> Local() => 1;
                    await Task.Yield();
                    return await Local();
                }
            }
            """;

        var diagnostics = await GetCS1998DiagnosticsAsync(source);

        Assert.Equal(4, diagnostics.Length);
        Assert.True(diagnostics[0].IsSuppressed);
        Assert.False(diagnostics[1].IsSuppressed);
        Assert.True(diagnostics[2].IsSuppressed);
        Assert.False(diagnostics[3].IsSuppressed);
    }

    [Fact]
    public async Task DoesNotSuppressCS1998WhenSuppressionIsDisabled()
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions([new(DurableTaskAsyncMethodLacksAwaitSuppressor.SuppressionId, ReportDiagnostic.Suppress)]);

        var diagnostics = await GetCS1998DiagnosticsAsync(Usings + "public class C { public async DurableTask<int> Get() => 1; }", options);

        var diagnostic = Assert.Single(diagnostics);
        Assert.False(diagnostic.IsSuppressed);
    }

    /// <summary>Compiles <paramref name="source"/> with the durable task stubs and returns its CS1998 diagnostics.</summary>
    private static async Task<Diagnostic[]> GetCS1998DiagnosticsAsync(string source, CSharpCompilationOptions? options = null)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source, path: "Test.cs"), CSharpSyntaxTree.ParseText(DurableTaskStubs.Source, path: "Stubs.cs")],
            AnalyzerVerifier.References,
            options ?? new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var analyzerOptions = new CompilationWithAnalyzersOptions(
            new AnalyzerOptions([]),
            onAnalyzerException: null,
            concurrentAnalysis: false,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: true);
        ImmutableArray<DiagnosticAnalyzer> analyzers = CompilerReportsCS1998
            ? [new DurableTaskAsyncMethodLacksAwaitSuppressor()]
            : [new LegacyAsyncLacksAwaitAnalyzer(), new DurableTaskAsyncMethodLacksAwaitSuppressor()];
        var diagnostics = await compilation
            .WithAnalyzers(analyzers, analyzerOptions)
            .GetAllDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        return diagnostics
            .Where(d => d.Id == DurableTaskAsyncMethodLacksAwaitSuppressor.SuppressedDiagnosticId && d.Location.SourceTree?.FilePath == "Test.cs")
            .OrderBy(d => d.Location.SourceSpan.Start)
            .ToArray();
    }

    private static string CreateTaskLikeTypesSource(string @namespace) => $$"""
        namespace {{@namespace}}
        {
            using System;
            using System.Runtime.CompilerServices;

            [AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
            public class DurableTask { }

            [AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
            public class DurableTask<TResult> : DurableTask { }

            public struct DurableTaskMethodBuilder
            {
                public static DurableTaskMethodBuilder Create() => default;
                public DurableTask Task => null!;
                public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine { }
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult() { }
                public void SetException(Exception exception) { }
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
            }

            public struct DurableTaskMethodBuilder<TResult>
            {
                public static DurableTaskMethodBuilder<TResult> Create() => default;
                public DurableTask<TResult> Task => null!;
                public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine { }
                public void SetStateMachine(IAsyncStateMachine stateMachine) { }
                public void SetResult(TResult result) { }
                public void SetException(Exception exception) { }
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
            }
        }

        """;

    /// <summary>
    /// Reports CS1998 for async functions without awaits at the locations used by compilers which report it:
    /// the identifier of a method or local function, the arrow of a lambda, and the <c>delegate</c> keyword of an anonymous method.
    /// </summary>
    /// <remarks>This test double is not a shipped compiler extension, and it reports the compiler's diagnostic ID intentionally.</remarks>
#pragma warning disable RS1029, RS1036, RS1038, RS1041, RS2008
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class LegacyAsyncLacksAwaitAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Descriptor = new(
            DurableTaskAsyncMethodLacksAwaitSuppressor.SuppressedDiagnosticId,
            "Async method lacks 'await' operators and will run synchronously",
            "This async method lacks 'await' operators and will run synchronously",
            "Compiler",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(
                Analyze,
                SyntaxKind.MethodDeclaration,
                SyntaxKind.LocalFunctionStatement,
                SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.SimpleLambdaExpression,
                SyntaxKind.AnonymousMethodExpression);
        }

        private static void Analyze(SyntaxNodeAnalysisContext context)
        {
            var (modifiers, body, location) = context.Node switch
            {
                MethodDeclarationSyntax method => (method.Modifiers, (SyntaxNode?)method.Body ?? method.ExpressionBody, method.Identifier.GetLocation()),
                LocalFunctionStatementSyntax function => (function.Modifiers, (SyntaxNode?)function.Body ?? function.ExpressionBody, function.Identifier.GetLocation()),
                LambdaExpressionSyntax lambda => (lambda.Modifiers, lambda.Body, lambda.ArrowToken.GetLocation()),
                AnonymousMethodExpressionSyntax method => (method.Modifiers, method.Body, method.DelegateKeyword.GetLocation()),
                _ => throw new InvalidOperationException($"Unexpected node {context.Node.Kind()}."),
            };

            var hasAwait = body is not null && body
                .DescendantNodes(node => node == body || node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .OfType<AwaitExpressionSyntax>()
                .Any();
            if (modifiers.Any(SyntaxKind.AsyncKeyword) && !hasAwait)
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, location));
            }
        }
    }
#pragma warning restore RS1029, RS1036, RS1038, RS1041, RS2008
}
