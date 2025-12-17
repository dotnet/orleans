using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Orleans.Analyzers;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Xunit;

namespace Analyzers.Tests;

/// <summary>
/// Tests for the analyzer that warns against ConfigureAwait(false) or ConfigureAwait without
/// ContinueOnCapturedContext in grain code. Grains must maintain their synchronization context
/// to ensure proper execution within the grain's activation context.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class ConfigureAwaitAnalyzerTest : DiagnosticAnalyzerTestBase<ConfigureAwaitAnalyzer>
{
    private static readonly string[] Usings = new[] {
        "System",
        "System.Threading.Tasks",
        "Orleans"
    };

    private async Task VerifyHasDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());

        Assert.NotEmpty(diagnostics);
        var diagnostic = diagnostics.First();

        Assert.Equal(ConfigureAwaitAnalyzer.RuleId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    private async Task VerifyHasNoDiagnostic(string code)
    {
        var (diagnostics, _) = await GetDiagnosticsAsync(code, Array.Empty<string>());
        Assert.Empty(diagnostics);
    }

    private async Task VerifyCodeFix(string originalCode, string expectedFixedCode, string[] extraUsings = null)
    {
        extraUsings ??= Array.Empty<string>();

        // Prepend usings
        var sb = new StringBuilder();
        foreach (var @using in Usings.Concat(extraUsings))
        {
            sb.AppendLine($"using {@using};");
        }
        sb.AppendLine(originalCode);
        var fullOriginalCode = sb.ToString();

        sb.Clear();
        foreach (var @using in Usings.Concat(extraUsings))
        {
            sb.AppendLine($"using {@using};");
        }
        sb.AppendLine(expectedFixedCode);
        var fullExpectedCode = sb.ToString();

        // Create project and get diagnostics
        var project = CreateProject(fullOriginalCode);
        var document = project.Documents.First();
        var compilation = await project.GetCompilationAsync();

        var analyzer = new ConfigureAwaitAnalyzer();
        var compilationWithAnalyzers = compilation
            .WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                analyzer.SupportedDiagnostics.ToDictionary(d => d.Id, d => ReportDiagnostic.Default)))
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        Assert.NotEmpty(diagnostics);

        // Apply code fix
        var codeFixer = new ConfigureAwaitCodeFix();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostics.First(),
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFixer.RegisterCodeFixesAsync(context);
        Assert.NotEmpty(actions);

        var operations = await actions.First().GetOperationsAsync(CancellationToken.None);
        var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var changedDocument = changedSolution.GetDocument(document.Id);
        var changedText = await changedDocument.GetTextAsync();

        Assert.Equal(fullExpectedCode, changedText.ToString());
    }

    private static Project CreateProject(string source)
    {
        const string fileName = "Test.cs";

        var projectId = ProjectId.CreateNewId(debugName: "TestProject");
        var documentId = DocumentId.CreateNewId(projectId, fileName);

        var assemblies = new[]
        {
            typeof(Task).Assembly,
            typeof(Orleans.IGrain).Assembly,
            typeof(Orleans.Grain).Assembly,
            typeof(Attribute).Assembly,
            typeof(int).Assembly,
            typeof(object).Assembly,
        };

        var metadataReferences = assemblies
            .SelectMany(x => x.GetReferencedAssemblies().Select(Assembly.Load))
            .Concat(assemblies)
            .Distinct()
            .Select(x => MetadataReference.CreateFromFile(x.Location))
            .Cast<MetadataReference>()
            .ToList();

        var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));

        var solution = new AdhocWorkspace()
            .CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .AddMetadataReferences(projectId, metadataReferences)
            .AddDocument(documentId, fileName, SourceText.From(source));

        return solution.GetProject(projectId)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    #region ConfigureAwait(false) in Grain

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a generic grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InGenericGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain<MyState>, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }

                    public class MyState { }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(true) in a grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitTrue_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(true);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region ConfigureAwait(false) in non-grain class

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a plain class (no inheritance) does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InPlainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyService
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a class implementing a non-grain interface does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InClassImplementingNonGrainInterface_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public interface IMyService
                    {
                        Task DoSomething();
                    }

                    public class MyService : IMyService
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a class inheriting from a non-grain base class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InClassInheritingNonGrainBase_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class BaseService
                    {
                    }

                    public class MyService : BaseService
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a class with deep non-grain inheritance does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InClassWithDeepNonGrainInheritance_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class GrandparentService
                    {
                    }

                    public class ParentService : GrandparentService
                    {
                    }

                    public class MyService : ParentService
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a struct does not trigger a diagnostic.
    /// Structs cannot be grains.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InStruct_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public struct MyStruct
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a record does not trigger a diagnostic.
    /// Records cannot be grains (they don't inherit from Grain or implement IGrainBase).
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InRecord_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public record MyRecord
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a record struct does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InRecordStruct_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public record struct MyRecordStruct
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a class implementing IDisposable (not a grain interface) does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InClassImplementingIDisposable_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyService : IDisposable
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }

                        public void Dispose() { }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a static class does not trigger a diagnostic.
    /// Static classes cannot be grains.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InStaticClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public static class MyStaticHelper
                    {
                        public static async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in an abstract non-grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InAbstractNonGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public abstract class MyAbstractService
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a generic non-grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InGenericNonGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyGenericService<T>
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a nested class inside a non-grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InNestedClassInsideNonGrain_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class OuterService
                    {
                        public class InnerService
                        {
                            public async Task DoSomething()
                            {
                                await Task.Delay(100).ConfigureAwait(false);
                            }
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions) without ContinueOnCapturedContext 
    /// in a non-grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_ForceYielding_InNonGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyService
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region No ConfigureAwait

    /// <summary>
    /// Verifies that awaiting without ConfigureAwait in a grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task NoConfigureAwait_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region IGrainBase implementation

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a class implementing IGrainBase triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InIGrainBaseImplementation_ShouldTriggerDiagnostic()
    {
        var code = """
                    using Orleans.Runtime;

                    public class MyGrain : IGrainBase, IMyGrain
                    {
                        public IGrainContext GrainContext { get; }

                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    #endregion

    #region ISystemTarget implementation

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a class implementing ISystemTarget triggers a diagnostic.
    /// ISystemTarget is in the Orleans namespace (not Orleans.Runtime), defined in Orleans.Core.Abstractions.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InISystemTargetImplementation_ShouldTriggerDiagnostic()
    {
        // Note: ISystemTarget is defined in namespace Orleans (in Orleans.Core.Abstractions assembly),
        // so no additional using is needed since we already have "using Orleans;"
        var code = """
                    public class MySystemTarget : ISystemTarget
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    #endregion

    #region Nested classes and lambdas

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a lambda within a grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InLambdaInsideGrain_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public Task DoSomething()
                        {
                            Func<Task> action = async () =>
                            {
                                await Task.Delay(100).ConfigureAwait(false);
                            };
                            return action();
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a nested class within a grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InNestedClassInsideGrain_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public Task DoSomething() => Task.CompletedTask;

                        private class NestedClass
                        {
                            public async Task DoWork()
                            {
                                await Task.Delay(100).ConfigureAwait(false);
                            }
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        // The nested class is inside a grain class, so it should still trigger
        return VerifyHasDiagnostic(code);
    }

    #endregion

    #region Inherited grain classes

    /// <summary>
    /// Verifies that ConfigureAwait(false) in a class that inherits from another grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_InInheritedGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class BaseGrain : Grain
                    {
                    }

                    public class MyGrain : BaseGrain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    #endregion

    #region ValueTask

    /// <summary>
    /// Verifies that ConfigureAwait(false) on ValueTask in a grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_OnValueTask_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await GetValueAsync().ConfigureAwait(false);
                        }

                        private ValueTask GetValueAsync() => ValueTask.CompletedTask;
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) on ValueTask&lt;T&gt; in a grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_OnGenericValueTask_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            var result = await GetValueAsync().ConfigureAwait(false);
                        }

                        private ValueTask<int> GetValueAsync() => ValueTask.FromResult(42);
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(true) on ValueTask in a grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitTrue_OnValueTask_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await GetValueAsync().ConfigureAwait(true);
                        }

                        private ValueTask GetValueAsync() => ValueTask.CompletedTask;
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) on ValueTask in a non-grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_OnValueTask_InNonGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyService
                    {
                        public async Task DoSomething()
                        {
                            await GetValueAsync().ConfigureAwait(false);
                        }

                        private ValueTask GetValueAsync() => ValueTask.CompletedTask;
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region IAsyncEnumerable

    /// <summary>
    /// Verifies that ConfigureAwait(false) on IAsyncEnumerable in await foreach in a grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_OnIAsyncEnumerable_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    using System.Collections.Generic;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await foreach (var item in GetItemsAsync().ConfigureAwait(false))
                            {
                                // Process item
                            }
                        }

                        private async IAsyncEnumerable<int> GetItemsAsync()
                        {
                            yield return 1;
                            await Task.Delay(1);
                            yield return 2;
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(true) on IAsyncEnumerable in await foreach in a grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitTrue_OnIAsyncEnumerable_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Collections.Generic;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await foreach (var item in GetItemsAsync().ConfigureAwait(true))
                            {
                                // Process item
                            }
                        }

                        private async IAsyncEnumerable<int> GetItemsAsync()
                        {
                            yield return 1;
                            await Task.Delay(1);
                            yield return 2;
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(false) on IAsyncEnumerable in a non-grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_OnIAsyncEnumerable_InNonGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Collections.Generic;

                    public class MyService
                    {
                        public async Task DoSomething()
                        {
                            await foreach (var item in GetItemsAsync().ConfigureAwait(false))
                            {
                                // Process item
                            }
                        }

                        private async IAsyncEnumerable<int> GetItemsAsync()
                        {
                            yield return 1;
                            await Task.Delay(1);
                            yield return 2;
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that await foreach without ConfigureAwait in a grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task NoConfigureAwait_OnIAsyncEnumerable_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Collections.Generic;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await foreach (var item in GetItemsAsync())
                            {
                                // Process item
                            }
                        }

                        private async IAsyncEnumerable<int> GetItemsAsync()
                        {
                            yield return 1;
                            await Task.Delay(1);
                            yield return 2;
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region Task<T>

    /// <summary>
    /// Verifies that ConfigureAwait(false) on Task&lt;T&gt; in a grain class triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitFalse_OnGenericTask_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            var result = await Task.FromResult(42).ConfigureAwait(false);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(true) on Task&lt;T&gt; in a grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitTrue_OnGenericTask_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            var result = await Task.FromResult(42).ConfigureAwait(true);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region ConfigureAwait(ConfigureAwaitOptions)

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions.None) in a grain class triggers a diagnostic
    /// because it doesn't include ContinueOnCapturedContext.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_None_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.None);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions.ForceYielding) in a grain class triggers a diagnostic
    /// because it doesn't include ContinueOnCapturedContext.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_ForceYielding_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing) in a grain class triggers a diagnostic
    /// because it doesn't include ContinueOnCapturedContext.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_SuppressThrowing_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext) in a grain class
    /// does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_ContinueOnCapturedContext_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait with combined flags including ContinueOnCapturedContext
    /// does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_CombinedWithContinueOnCapturedContext_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait with combined flags NOT including ContinueOnCapturedContext
    /// triggers a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_CombinedWithoutContinueOnCapturedContext_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions) in a non-grain class does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_None_InNonGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyService
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.None);
                        }
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions) on Task&lt;T&gt; works correctly.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_None_OnGenericTask_InGrainClass_ShouldTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            var result = await Task.FromResult(42).ConfigureAwait(ConfigureAwaitOptions.None);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasDiagnostic(code);
    }

    /// <summary>
    /// Verifies that ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext) on Task&lt;T&gt; does not trigger a diagnostic.
    /// </summary>
    [Fact]
    public Task ConfigureAwaitOptions_ContinueOnCapturedContext_OnGenericTask_InGrainClass_ShouldNotTriggerDiagnostic()
    {
        var code = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            var result = await Task.FromResult(42).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyHasNoDiagnostic(code);
    }

    #endregion

    #region Code Fix Tests

    /// <summary>
    /// Verifies that the code fix converts ConfigureAwait(false) to ConfigureAwait(true).
    /// </summary>
    [Fact]
    public Task CodeFix_ConfigureAwaitFalse_ChangesToTrue()
    {
        var originalCode = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        var expectedFixedCode = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(true);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyCodeFix(originalCode, expectedFixedCode);
    }

    /// <summary>
    /// Verifies that the code fix converts ConfigureAwait(false) to ConfigureAwait(true) on ValueTask.
    /// </summary>
    [Fact]
    public Task CodeFix_ConfigureAwaitFalse_OnValueTask_ChangesToTrue()
    {
        var originalCode = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await GetValueAsync().ConfigureAwait(false);
                        }

                        private ValueTask GetValueAsync() => ValueTask.CompletedTask;
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        var expectedFixedCode = """
                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await GetValueAsync().ConfigureAwait(true);
                        }

                        private ValueTask GetValueAsync() => ValueTask.CompletedTask;
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyCodeFix(originalCode, expectedFixedCode);
    }

    /// <summary>
    /// Verifies that the code fix converts ConfigureAwait(ConfigureAwaitOptions.None) to ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext).
    /// </summary>
    [Fact]
    public Task CodeFix_ConfigureAwaitOptionsNone_ChangesToContinueOnCapturedContext()
    {
        var originalCode = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.None);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        var expectedFixedCode = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyCodeFix(originalCode, expectedFixedCode);
    }

    /// <summary>
    /// Verifies that the code fix adds ContinueOnCapturedContext to ConfigureAwait(ConfigureAwaitOptions.ForceYielding).
    /// </summary>
    [Fact]
    public Task CodeFix_ConfigureAwaitOptionsForceYielding_AddsContinueOnCapturedContext()
    {
        var originalCode = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        var expectedFixedCode = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyCodeFix(originalCode, expectedFixedCode);
    }

    /// <summary>
    /// Verifies that the code fix adds ContinueOnCapturedContext to combined ConfigureAwaitOptions.
    /// </summary>
    [Fact]
    public Task CodeFix_ConfigureAwaitOptionsCombined_AddsContinueOnCapturedContext()
    {
        var originalCode = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        var expectedFixedCode = """
                    using System.Threading.Tasks;

                    public class MyGrain : Grain, IMyGrain
                    {
                        public async Task DoSomething()
                        {
                            await Task.Delay(100).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                        }
                    }

                    public interface IMyGrain : IGrainWithGuidKey
                    {
                        Task DoSomething();
                    }
                    """;

        return VerifyCodeFix(originalCode, expectedFixedCode);
    }

    #endregion
}
