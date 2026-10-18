using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Tests that <c>DurableTask.Once</c> accepts synchronous effects and rejects asynchronous and durable effects at compile
/// time, which it can't record.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskOnceOverloadTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. new[] { typeof(object).Assembly, typeof(Task).Assembly, typeof(DurableTask).Assembly }
            .SelectMany(assembly => assembly.GetReferencedAssemblies().Select(Assembly.Load).Append(assembly))
            .Distinct()
            .Select(assembly => MetadataReference.CreateFromFile(assembly.Location)),
    ];

    public static TheoryData<string> SynchronousEffects => new()
    {
        "int value = DurableTask.Once(\"a\", () => 1);",
        "DurableTask.Once(\"a\", () => { });",
        "Guid id = DurableTask.Once(\"a\", Guid.NewGuid);",
        "DurableTask.Once(\"a\", () => { if (Environment.ProcessorCount > 0) throw new InvalidOperationException(); });",
        "int value = DurableTask.Once(\"a\", Compute);",
        "int counter = 0; int value = DurableTask.Once(\"a\", () => counter++);",
        "int value = DurableTask.Once(() => 1);",
        "DurableTask.Once(() => { });",
        "Guid id = DurableTask.Once(Guid.NewGuid);",
        "DurableTask.Once(() => { if (Environment.ProcessorCount > 0) throw new InvalidOperationException(); });",
        "int value = DurableTask.Once(Compute);",
        "string value = DurableTask.Once(() => \"a\");",
        "DurableTask.Once(Mark);",
        "DurableTask.Once(() => Mark());",

        // A lambda whose expression has a value converts to Action and to Func<T>; the effect returns the value.
        "int counter = 0; int value = DurableTask.Once(() => counter++);",
        "int value = DurableTask.Once(() => Compute());",
        "_ = DurableTask.Once(() => 1);",

        // A lambda which only throws also converts to Func<Task>, which the rejecting overload takes; a cast selects Action.
        "DurableTask.Once((Action)(() => throw new InvalidOperationException()));",
        "DurableTask.Once(\"a\", (Action)(() => { throw new InvalidOperationException(); }));",

        // Explicit type arguments select the synchronous overload, whose result-type check rejects the effect when it runs.
        "_ = DurableTask.Once<Task<int>>(() => Task.FromResult(1));",
    };

    private const string UseRun = "For asynchronous work, use await DurableTask.Run(async () => ...).";
    private const string AwaitDirectly = "Await the durable task directly.";

    public static TheoryData<string, string> AsynchronousEffects => new()
    {
        { "_ = DurableTask.Once(\"a\", async () => { await Task.Yield(); return 1; });", UseRun },
        { "DurableTask.Once(\"a\", async () => await Task.Yield());", UseRun },
        { "_ = DurableTask.Once(\"a\", () => Task.FromResult(1));", UseRun },
        { "_ = DurableTask.Once(\"a\", ComputeAsync);", UseRun },
        { "_ = DurableTask.Once(\"a\", () => DurableTask.FromResult(1));", AwaitDirectly },
        { "_ = DurableTask.Once(\"a\", Step);", AwaitDirectly },
        { "_ = DurableTask.Once(async () => { await Task.Yield(); return 1; });", UseRun },
        { "DurableTask.Once(async () => await Task.Yield());", UseRun },
        { "DurableTask.Once(async () => { });", UseRun },
        { "_ = DurableTask.Once(() => Task.FromResult(1));", UseRun },
        { "DurableTask.Once(() => Task.CompletedTask);", UseRun },
        { "_ = DurableTask.Once(ComputeAsync);", UseRun },
        { "_ = DurableTask.Once(() => DurableTask.FromResult(1));", AwaitDirectly },
        { "_ = DurableTask.Once(Step);", AwaitDirectly },
        { "DurableTask.Once(Run);", AwaitDirectly },
        { "_ = DurableTask.Once(() => ValueTask.FromResult(1));", UseRun },
        { "_ = DurableTask.Once(\"a\", () => ValueTask.FromResult(1));", UseRun },
        { "_ = DurableTask.Once(ComputeValueAsync);", UseRun },
        { "DurableTask.Once(() => ValueTask.CompletedTask);", UseRun },
        { "DurableTask.Once(\"a\", MarkValueAsync);", UseRun },

        // An asynchronous lambda without a result also converts to Action, which would run as async void.
        { "DurableTask.Once(\"a\", async () => { await Task.Yield(); });", UseRun },

        // A lambda which only throws also converts to Func<Task>; casting it to Action records the exception.
        { "DurableTask.Once(() => throw new InvalidOperationException());", UseRun },
        { "DurableTask.Once(\"a\", () => { throw new InvalidOperationException(); });", UseRun },
    };

    [Theory]
    [MemberData(nameof(SynchronousEffects))]
    public void SynchronousEffectsCompile(string statement)
    {
        Assert.Empty(GetErrors(statement));
    }

    [Theory]
    [MemberData(nameof(AsynchronousEffects))]
    public void AsynchronousAndDurableEffectsFailWithGuidance(string statement, string guidance)
    {
        var error = Assert.Single(GetErrors(statement));
        if (CompilerSupportsOverloadPriority || error.Id != "CS0121")
        {
            Assert.Equal("CS0619", error.Id);
            Assert.Contains("DurableTask.Once stores a synchronous result", error.GetMessage(), StringComparison.Ordinal);
            Assert.Contains(guidance, error.GetMessage(), StringComparison.Ordinal);
        }
    }
    // Compilers which predate C# 13 ignore OverloadResolutionPriorityAttribute, so they reject an asynchronous lambda as
    // ambiguous between the rejecting overloads rather than selecting the one which carries the guidance.
    private static bool CompilerSupportsOverloadPriority => Enum.TryParse<LanguageVersion>("CSharp13", out _);

    private static Diagnostic[] GetErrors(string statement)
    {
        var source = $$"""
            using System;
            using System.Threading.Tasks;
            using Orleans.DurableTasks;

            public class C
            {
                public void M()
                {
                    {{statement}}
                }

                private static int Compute() => 1;

                private static void Mark() { }

                private static Task<int> ComputeAsync() => Task.FromResult(1);

                private static ValueTask<int> ComputeValueAsync() => ValueTask.FromResult(1);

                private static ValueTask MarkValueAsync() => ValueTask.CompletedTask;

                private static async DurableTask<int> Step() => 1;

                private static async DurableTask Run() { }
            }
            """;
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: "Test.cs")],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    }
}
