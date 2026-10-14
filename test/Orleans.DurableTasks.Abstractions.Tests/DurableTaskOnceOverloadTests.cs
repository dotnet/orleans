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
    };

    public static TheoryData<string> AsynchronousEffects => new()
    {
        "_ = DurableTask.Once(\"a\", async () => { await Task.Yield(); return 1; });",
        "DurableTask.Once(\"a\", async () => await Task.Yield());",
        "_ = DurableTask.Once(\"a\", () => Task.FromResult(1));",
        "_ = DurableTask.Once(\"a\", ComputeAsync);",
        "_ = DurableTask.Once(\"a\", () => DurableTask.FromResult(1));",
        "_ = DurableTask.Once(\"a\", Step);",
        "_ = DurableTask.Once(async () => { await Task.Yield(); return 1; });",
        "DurableTask.Once(async () => await Task.Yield());",
        "DurableTask.Once(async () => { });",
        "_ = DurableTask.Once(() => Task.FromResult(1));",
        "DurableTask.Once(() => Task.CompletedTask);",
        "_ = DurableTask.Once(ComputeAsync);",
        "_ = DurableTask.Once(() => DurableTask.FromResult(1));",
        "_ = DurableTask.Once(Step);",
        "DurableTask.Once(Run);",
    };

    [Theory]
    [MemberData(nameof(SynchronousEffects))]
    public void SynchronousEffectsCompile(string statement)
    {
        Assert.Empty(GetErrors(statement));
    }

    [Theory]
    [MemberData(nameof(AsynchronousEffects))]
    public void AsynchronousAndDurableEffectsFailWithGuidance(string statement)
    {
        var error = Assert.Single(GetErrors(statement));
        if (CompilerSupportsOverloadPriority || error.Id != "CS0121")
        {
            Assert.Equal("CS0619", error.Id);
            Assert.Contains("DurableTask.Once runs a synchronous effect", error.GetMessage(), StringComparison.Ordinal);
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
