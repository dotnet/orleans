using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>
/// Tests that <c>DurableTask.RunInline</c> accepts synchronous effects and rejects asynchronous and durable effects at compile
/// time, which it can't record.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskRunInlineOverloadTests
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
        "int value = DurableTask.RunInline(\"a\", () => 1);",
        "DurableTask.RunInline(\"a\", () => { });",
        "Guid id = DurableTask.RunInline(\"a\", Guid.NewGuid);",
        "DurableTask.RunInline(\"a\", () => { if (Environment.ProcessorCount > 0) throw new InvalidOperationException(); });",
        "int value = DurableTask.RunInline(\"a\", Compute);",
        "int counter = 0; int value = DurableTask.RunInline(\"a\", () => counter++);",
        "int value = DurableTask.RunInline(() => 1);",
        "DurableTask.RunInline(() => { });",
        "Guid id = DurableTask.RunInline(Guid.NewGuid);",
        "DurableTask.RunInline(() => { if (Environment.ProcessorCount > 0) throw new InvalidOperationException(); });",
        "int value = DurableTask.RunInline(Compute);",
        "string value = DurableTask.RunInline(() => \"a\");",
        "DurableTask.RunInline(Mark);",
        "DurableTask.RunInline(() => Mark());",

        // A lambda whose expression has a value converts to Action and to Func<T>; the effect returns the value.
        "int counter = 0; int value = DurableTask.RunInline(() => counter++);",
        "int value = DurableTask.RunInline(() => Compute());",
        "_ = DurableTask.RunInline(() => 1);",

        // A lambda which only throws also converts to Func<Task>, which the rejecting overload takes; a cast selects Action.
        "DurableTask.RunInline((Action)(() => throw new InvalidOperationException()));",
        "DurableTask.RunInline(\"a\", (Action)(() => { throw new InvalidOperationException(); }));",

        // Explicit type arguments select the synchronous overload, whose result-type check rejects the effect when it runs.
        "_ = DurableTask.RunInline<Task<int>>(() => Task.FromResult(1));",
    };

    private const string UseRun = "For asynchronous work, use await DurableTask.Run(async () => ...).";
    private const string AwaitDirectly = "Await the durable task directly.";

    public static TheoryData<string, string> AsynchronousEffects => new()
    {
        { "_ = DurableTask.RunInline(\"a\", async () => { await Task.Yield(); return 1; });", UseRun },
        { "DurableTask.RunInline(\"a\", async () => await Task.Yield());", UseRun },
        { "_ = DurableTask.RunInline(\"a\", () => Task.FromResult(1));", UseRun },
        { "_ = DurableTask.RunInline(\"a\", ComputeAsync);", UseRun },
        { "_ = DurableTask.RunInline(\"a\", () => DurableTask.FromResult(1));", AwaitDirectly },
        { "_ = DurableTask.RunInline(\"a\", Step);", AwaitDirectly },
        { "_ = DurableTask.RunInline(async () => { await Task.Yield(); return 1; });", UseRun },
        { "DurableTask.RunInline(async () => await Task.Yield());", UseRun },
        { "DurableTask.RunInline(async () => { });", UseRun },
        { "_ = DurableTask.RunInline(() => Task.FromResult(1));", UseRun },
        { "DurableTask.RunInline(() => Task.CompletedTask);", UseRun },
        { "_ = DurableTask.RunInline(ComputeAsync);", UseRun },
        { "_ = DurableTask.RunInline(() => DurableTask.FromResult(1));", AwaitDirectly },
        { "_ = DurableTask.RunInline(Step);", AwaitDirectly },
        { "DurableTask.RunInline(Run);", AwaitDirectly },
        { "_ = DurableTask.RunInline(() => ValueTask.FromResult(1));", UseRun },
        { "_ = DurableTask.RunInline(\"a\", () => ValueTask.FromResult(1));", UseRun },
        { "_ = DurableTask.RunInline(ComputeValueAsync);", UseRun },
        { "DurableTask.RunInline(() => ValueTask.CompletedTask);", UseRun },
        { "DurableTask.RunInline(\"a\", MarkValueAsync);", UseRun },

        // An asynchronous lambda without a result also converts to Action, which would run as async void.
        { "DurableTask.RunInline(\"a\", async () => { await Task.Yield(); });", UseRun },

        // A lambda which only throws also converts to Func<Task>; casting it to Action records the exception.
        { "DurableTask.RunInline(() => throw new InvalidOperationException());", UseRun },
        { "DurableTask.RunInline(\"a\", () => { throw new InvalidOperationException(); });", UseRun },
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
            Assert.Contains("DurableTask.RunInline stores a synchronous result", error.GetMessage(), StringComparison.Ordinal);
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
