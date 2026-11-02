using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Tests which <c>DurableTask.Run</c> overload each delegate shape binds to, and that delegates which return durable tasks
/// fail at compile time with guidance.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskRunOverloadTests
{
    private const string AsyncFunction = "System.Func<System.Threading.Tasks.Task<TResult>>";
    private const string AsyncAction = "System.Func<System.Threading.Tasks.Task>";
    private const string ValueFunction = "System.Func<System.Threading.Tasks.ValueTask<TResult>>";
    private const string ValueAction = "System.Func<System.Threading.Tasks.ValueTask>";
    private const string TokenAsyncFunction = "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<TResult>>";
    private const string TokenAsyncAction = "System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task>";

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. new[] { typeof(object).Assembly, typeof(Task).Assembly, typeof(DurableTask).Assembly }
            .SelectMany(assembly => assembly.GetReferencedAssemblies().Select(Assembly.Load).Append(assembly))
            .Distinct()
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location)),
    ];

    public static TheoryData<string, string> Resolutions => new()
    {
        { "var step = DurableTask.Run(async () => { await Task.Yield(); return 1; });", AsyncFunction },
        { "var step = DurableTask.Run(async () => await ComputeAsync());", AsyncFunction },
        { "var step = DurableTask.Run(ComputeAsync);", AsyncFunction },
        { "var step = DurableTask.Run(() => ComputeAsync());", AsyncFunction },
        { "Guid id = await DurableTask.Run(async () => Guid.NewGuid()).WithId(\"get-id\");", AsyncFunction },
        { "_ = DurableTask.Run(async () => 1);", AsyncFunction },
        { "var step = DurableTask.Run<int>(async () => 1);", AsyncFunction },
        { "var step = DurableTask.Run(async () => await Task.Yield());", AsyncAction },
        { "var step = DurableTask.Run(async () => { });", AsyncAction },
        { "var step = DurableTask.Run(() => Task.CompletedTask);", AsyncAction },
        { "var step = DurableTask.Run(() => throw new InvalidOperationException());", AsyncAction },
        { "var step = DurableTask.Run(() => ComputeValueAsync());", ValueFunction },
        { "var step = DurableTask.Run(ComputeValueAsync);", ValueFunction },
        { "var step = DurableTask.Run(MarkValueAsync);", ValueAction },
        { "var step = DurableTask.Run(async ct => { await Task.Delay(1, ct); return 1; });", TokenAsyncFunction },
        { "var step = DurableTask.Run(async ct => await Task.Delay(1, ct));", TokenAsyncAction },
        { "var step = DurableTask.Run(ComputeWithTokenAsync);", TokenAsyncFunction },
        { "var step = DurableTask.Run(ComputeWithOptionalTokenAsync);", TokenAsyncFunction },
        { "var step = DurableTask.Run(ct => 1);", "System.Func<System.Threading.CancellationToken, TResult>" },
        { "var step = DurableTask.Run(_ => { });", "System.Action<System.Threading.CancellationToken>" },

        // An asynchronous lambda typed as Action<CancellationToken> runs as async void, so the step completes when the delegate first yields.
        { "Action<CancellationToken> a = async ct => await Task.Delay(1, ct); var step = DurableTask.Run(a);", "System.Action<System.Threading.CancellationToken>" },
    };

    public static TheoryData<string> DurableTaskDelegates => new()
    {
        "_ = DurableTask.Run(() => DurableTask.FromResult(1));",
        "_ = DurableTask.Run(Step);",
        "_ = DurableTask.Run(RunStep);",
        "_ = DurableTask.Run(() => RunStep());",
        "_ = DurableTask.Run(_ => Step());",
        "_ = DurableTask.Run(_ => RunStep());",
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void DelegateShapesBindTheirOverload(string statement, string parameterType)
    {
        var (compilation, tree) = Compile(statement);
        var errors = GetErrors(compilation);

        // Compilers which predate C# 13 ignore OverloadResolutionPriorityAttribute, so they reject an asynchronous lambda as
        // ambiguous between overloads which the attribute orders.
        if (!CompilerSupportsOverloadPriority && errors is [{ Id: "CS0121" }])
        {
            return;
        }

        Assert.Empty(errors);
        var invocation = tree.GetRoot(Xunit.TestContext.Current.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First(node => node.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Run" } or MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "Run" } });
        var method = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(tree).GetSymbolInfo(invocation, Xunit.TestContext.Current.CancellationToken).Symbol);
        Assert.Equal(parameterType, Assert.Single(method.OriginalDefinition.Parameters).Type.ToDisplayString());
    }

    [Theory]
    [MemberData(nameof(DurableTaskDelegates))]
    public void DurableTaskDelegatesFailWithGuidance(string statement)
    {
        var error = Assert.Single(GetErrors(Compile(statement).Compilation));
        Assert.Equal("CS0619", error.Id);
        Assert.Contains("Await the durable task directly", error.GetMessage(), StringComparison.Ordinal);
    }

    private static bool CompilerSupportsOverloadPriority => Enum.TryParse<LanguageVersion>("CSharp13", out _);

    private static Diagnostic[] GetErrors(Compilation compilation) =>
        compilation.GetDiagnostics(Xunit.TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

    private static (CSharpCompilation Compilation, SyntaxTree Tree) Compile(string statement)
    {
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Orleans.DurableTasks;

            public class C
            {
                public async DurableTask M()
                {
                    {{statement}}
                }

                private static Task<int> ComputeAsync() => Task.FromResult(1);

                private static Task<int> ComputeWithTokenAsync(CancellationToken cancellationToken) => Task.FromResult(1);

                private static Task<int> ComputeWithOptionalTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(1);

                private static ValueTask<int> ComputeValueAsync() => ValueTask.FromResult(1);

                private static ValueTask MarkValueAsync() => ValueTask.CompletedTask;

                private static async DurableTask<int> Step() => 1;

                private static async DurableTask RunStep() { }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation, tree);
    }
}
