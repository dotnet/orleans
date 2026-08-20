#nullable enable

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
/// Tests for the analyzer that tracks grain interface definitions and their versions.
/// This analyzer ensures that interface changes are accompanied by version increments
/// for compatibility during rolling upgrades.
/// </summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class GrainInterfaceVersionAnalyzerTest
{
    private const string OrleansContractsFileName = "OrleansContracts.txt";

    private static readonly string[] Usings = new[]
    {
        "System",
        "System.Threading.Tasks",
        "Orleans",
        "Orleans.CodeGeneration",
        "Orleans.Runtime"
    };

    #region Test Infrastructure

    private async Task<Diagnostic[]> GetDiagnosticsAsync(
        string source,
        string? grainInterfacesFileContent = null,
        bool analyzerEnabled = true)
    {
        var project = CreateProjectWithAdditionalFiles(source, grainInterfacesFileContent);
        var compilation = await project.GetCompilationAsync();
        var errors = compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);

        Assert.Empty(errors);

        var analyzer = new GrainInterfaceVersionAnalyzer();

        // Build analyzer options with additional files
        var additionalFiles = grainInterfacesFileContent is not null
            ? ImmutableArray.Create<AdditionalText>(new TestAdditionalText(OrleansContractsFileName, grainInterfacesFileContent))
            : ImmutableArray<AdditionalText>.Empty;

        var analyzerOptions = CreateAnalyzerOptions(additionalFiles, analyzerEnabled);

        var compilationWithAnalyzers = compilation
            .WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                analyzer.SupportedDiagnostics.ToDictionary(d => d.Id, d => ReportDiagnostic.Default)))
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer), analyzerOptions);

        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
    }

    private static Project CreateProjectWithAdditionalFiles(string source, string? grainInterfacesFileContent)
    {
        const string fileName = "Test.cs";

        // Prepend usings
        var sb = new StringBuilder();
        foreach (var @using in Usings)
        {
            sb.AppendLine($"using {@using};");
        }
        sb.AppendLine(source);
        var fullSource = sb.ToString();

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

        var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));

        var solution = new AdhocWorkspace()
            .CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .AddMetadataReferences(projectId, metadataReferences)
            .AddDocument(documentId, fileName, SourceText.From(fullSource));

        return solution.GetProject(projectId)!
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// A simple implementation of AdditionalText for testing purposes.
    /// </summary>
    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public TestAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static AnalyzerOptions CreateAnalyzerOptions(
        ImmutableArray<AdditionalText> additionalFiles,
        bool analyzerEnabled)
        => new(additionalFiles, new TestAnalyzerConfigOptionsProvider(analyzerEnabled));

    private sealed class TestAnalyzerConfigOptionsProvider(bool analyzerEnabled) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _globalOptions = new TestAnalyzerConfigOptions(
            ImmutableDictionary<string, string>.Empty.Add(
                $"build_property.{GrainInterfaceVersionAnalyzer.EnableAnalyzerPropertyName}",
                analyzerEnabled.ToString()));

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestAnalyzerConfigOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestAnalyzerConfigOptions.Empty;
    }

    private sealed class TestAnalyzerConfigOptions(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public static TestAnalyzerConfigOptions Empty { get; } = new(ImmutableDictionary<string, string>.Empty);

        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    #endregion

    #region ORLEANS0016 - Interface Not Declared

    [Fact]
    public async Task AnalyzerDisabled_NoDiagnostic()
    {
        const string source = @"
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";

        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFileContent: null, analyzerEnabled: false);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InterfaceNotInFile_WithExistingFile_ReportsDiagnostic()
    {
        const string source = @"
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        // Use *RETIRED* for the dummy interface so it doesn't trigger ORLEANS0019
        const string grainInterfacesFile = @"
# OrleansContracts.txt
*RETIRED* SomeOther.IGrain [Version(1)]
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceVersionAnalyzer.RuleId0016, diagnostics[0].Id);
        Assert.Contains("IMyGrain", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task InterfaceInFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        // No ORLEANS0016 diagnostic
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    [Fact]
    public async Task InterfaceWithAlias_InFile_NoDiagnostic()
    {
        const string source = @"
[Alias(""my-grain"")]
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
[Alias(""my-grain"")] IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    [Fact]
    public async Task InterfaceMarkedRetired_ReportsActiveDeclarationDiagnostic()
    {
        const string source = @"
public interface IMyGrain : IGrain
{
}
";
        const string grainInterfacesFile = @"
*RETIRED* IMyGrain [Version(0)]
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceVersionAnalyzer.RuleId0016, diagnostic.Id);
        Assert.Contains("does not have an active declaration", diagnostic.GetMessage());
    }

    #endregion

    #region ORLEANS0017 - Version Mismatch

    [Fact]
    public async Task VersionMismatch_ReportsDiagnostic()
    {
        const string source = @"
[Version(2)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
        var diagnostic = diagnostics.First(d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
        Assert.Contains("IMyGrain", diagnostic.GetMessage());
        Assert.Contains("1", diagnostic.GetMessage()); // Expected version
        Assert.Contains("2", diagnostic.GetMessage()); // Actual version
    }

    [Fact]
    public async Task VersionMatch_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
    }

    [Fact]
    public async Task NoVersionAttribute_DefaultsToZero()
    {
        const string source = @"
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(0)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
    }

    #endregion

    #region ORLEANS0018 - Member Not Declared

    [Fact]
    public async Task NewMember_NotInFile_ReportsDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
    Task NewMethod();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
        var diagnostic = diagnostics.First(d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
        Assert.Contains("NewMethod", diagnostic.GetMessage());
    }

    [Fact]
    public async Task MemberInFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task MemberWithParameters_InFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething(string name, int count);
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething(string name, int count) -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task LegacyMemberParameterRename_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething(string renamed, int newCount);
}
";
        const string contractsFile = @"
IMyGrain [Version(1)]
IMyGrain.DoSomething(string original, int oldCount) -> Task
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task MemberWithTupleParameter_InFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething((int X, int Y) value);
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething((int X, int Y) value) -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task MemberTypeNamespaceChanged_ReportsDiagnostic()
    {
        const string source = @"
namespace NamespaceA
{
    public sealed class Request { }
}

[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething(NamespaceA.Request request);
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething(NamespaceB.Request request) -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task StaticMember_NotInFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    static Task Utility() => Task.CompletedTask;
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task GenericMethod_InFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task<T> ReadStateAsync<T>(T value);
}
";
        const string contractsFile = @"
interface IMyGrain [Version(1)]
  ReadStateAsync`1(T) -> Task<T>
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task GenericMethodArityChanged_ReportsDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task<T1> ReadStateAsync<T1, T2>(T1 first, T2 second);
}
";
        const string contractsFile = @"
interface IMyGrain [Version(1)]
  ReadStateAsync`1(T) -> Task<T>
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    #endregion

    #region ORLEANS0019 - Removed Interface Not Retired

    [Fact]
    public async Task RemovedInterface_NotRetired_ReportsDiagnostic()
    {
        const string source = @"
// No interfaces in code
public class SomeClass { }
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IOldGrain [Version(1)]
IOldGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0019);
        var diagnostic = diagnostics.First(d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0019);
        Assert.Contains("IOldGrain", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RetiredInterface_NoDiagnostic()
    {
        const string source = @"
// No interfaces in code
public class SomeClass { }
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
*RETIRED* IOldGrain [Version(1)]
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0019);
    }

    #endregion

    #region ORLEANS0020 - File Missing

    [Fact]
    public async Task NoFile_WithGrainInterfaces_ReportsDiagnostic()
    {
        const string source = @"
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFileContent: null);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0020);
        Assert.Contains(OrleansContractsFileName, diagnostic.GetMessage());
    }

    [Fact]
    public async Task NoFile_NoGrainInterfaces_NoDiagnostic()
    {
        const string source = @"
// No grain interfaces, just regular code
public class RegularClass
{
    public void DoSomething() { }
}
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFileContent: null);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0020);
    }

    #endregion

    #region ORLEANS0021 - Duplicate Declaration

    [Fact]
    public async Task DuplicateInterfaceDeclaration_ReportsDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain [Version(2)]
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0021);
    }

    #endregion

    #region Non-Grain Interfaces

    [Fact]
    public async Task NonGrainInterface_NoDiagnostic()
    {
        const string source = @"
// Regular interface, not a grain
public interface IMyService
{
    void DoSomething();
}
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFileContent: null);

        // No diagnostics at all - non-grain interfaces are not tracked
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ObserverInterface_InFile_NoDiagnostic()
    {
        const string source = @"
public interface IMyObserver : IGrainObserver
{
    void OnEvent(string value);
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyObserver [Version(0)]
IMyObserver.OnEvent(string value) -> void
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.Empty(diagnostics);
    }

    #endregion

    #region Base Grain Interfaces

    [Fact]
    public async Task IGrainBase_NotTracked()
    {
        // Test that Orleans base interfaces like IGrain itself are not reported
        const string source = @"
// User code doesn't define IGrain - it comes from Orleans
public class SomeClass { }
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        // Should not report IGrain, IGrainWithStringKey, etc. as missing
        Assert.Empty(diagnostics);
    }

    #endregion

    #region Namespaced Interfaces

    [Fact]
    public async Task NamespacedInterface_InFile_NoDiagnostic()
    {
        const string source = @"
namespace MyApp.Grains
{
    [Version(1)]
    public interface IMyGrain : IGrain
    {
        Task DoSomething();
    }
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
MyApp.Grains.IMyGrain [Version(1)]
MyApp.Grains.IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    #endregion

    #region File Format

    [Fact]
    public async Task FileWithComments_ParsedCorrectly()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# This is a comment
# Another comment
IMyGrain [Version(1)]
# Comment between entries
IMyGrain.DoSomething() -> Task
# Final comment
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        // Should parse correctly despite comments
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task FileWithBlankLines_ParsedCorrectly()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"

IMyGrain [Version(1)]

IMyGrain.DoSomething() -> Task

";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    [Fact]
    public async Task VersionOutsideUShortRange_DoesNotCrashAnalyzer()
    {
        const string source = @"
public interface IMyGrain : IGrain
{
}
";
        const string grainInterfacesFile = @"
IMyGrain [Version(65536)]
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
    }

    #endregion

    #region Grain Class Contracts

    [Fact]
    public async Task GrainClassNotInFile_ReportsDiagnostic()
    {
        const string source = @"
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = "# OrleansContracts.txt\n";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceVersionAnalyzer.RuleId0022, diagnostic.Id);
        Assert.Contains("MyGrain", diagnostic.GetMessage());
    }

    [Fact]
    public async Task GrainClassWithMatchingAlias_NoDiagnostic()
    {
        const string source = @"
[GrainType(""my-grain"")]
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = @"
class [GrainType(""my-grain"")] MyGrain
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task GrainClassAliasMismatch_ReportsDiagnostic()
    {
        const string source = @"
[GrainType(""new-alias"")]
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = @"
class [GrainType(""old-alias"")] MyGrain
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceVersionAnalyzer.RuleId0023, diagnostic.Id);
        Assert.Contains("old-alias", diagnostic.GetMessage());
        Assert.Contains("new-alias", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RemovedGrainClass_NotRetired_ReportsDiagnostic()
    {
        const string source = "public class SomeClass { }";
        const string contractsFile = "class [GrainType(\"my-grain\")] MyGrain";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceVersionAnalyzer.RuleId0024, diagnostic.Id);
    }

    [Fact]
    public async Task AbstractGrainClass_IsNotTracked()
    {
        const string source = @"
public abstract class MyGrainBase : Grain
{
}
";
        const string contractsFile = "# OrleansContracts.txt\n";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task PocoGrainClass_IsTracked()
    {
        const string source = @"
public interface IPocoGrain : IGrain
{
}

public class PocoGrain : IGrainBase, IPocoGrain
{
    public Orleans.Runtime.IGrainContext GrainContext => throw new NotImplementedException();
}
";
        const string contractsFile = @"
IPocoGrain [Version(0)]
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == GrainInterfaceVersionAnalyzer.RuleId0022);
        Assert.Equal(GrainInterfaceVersionAnalyzer.RuleId0022, diagnostic.Id);
    }

    [Fact]
    public async Task DuplicateGrainClassDeclaration_ReportsDiagnostic()
    {
        const string source = @"
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = @"
class MyGrain
class MyGrain
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(GrainInterfaceVersionAnalyzer.RuleId0025, diagnostic.Id);
    }

    [Fact]
    public async Task GrainClassRename_WithStableGrainType_NoDiagnostic()
    {
        const string oldSource = @"
[GrainType(""stable-grain"")]
public class OldGrain : Grain, IGrainWithStringKey
{
}
";
        const string newSource = @"
[GrainType(""stable-grain"")]
public class NewGrain : Grain, IGrainWithStringKey
{
}
";
        var contractsFile = await ApplyCodeFixAndGetContractsAsync(
            oldSource,
            "# OrleansContracts.txt\n",
            GrainInterfaceVersionAnalyzer.RuleId0022);

        var diagnostics = await GetDiagnosticsAsync(newSource, contractsFile);

        Assert.Empty(diagnostics);
        Assert.Contains("# OldGrain\nclass [GrainType(\"stable-grain\")] OldGrain", contractsFile);
    }

    [Fact]
    public async Task RpcContractRefactor_WithStableIdentities_NoDiagnostic()
    {
        const string oldSource = @"
[Alias(""request"")]
public sealed class OldRequest { }

[Alias(""response"")]
public sealed class OldResponse { }

[GrainInterfaceType(""stable-interface"")]
public interface IOldGrain : IGrain
{
    [Alias(""stable-method"")]
    Task<OldResponse> OldMethod(OldRequest request);
}
";
        const string newSource = @"
[Alias(""request"")]
public sealed class NewRequest { }

[Alias(""response"")]
public sealed class NewResponse { }

[GrainInterfaceType(""stable-interface"")]
public interface INewGrain : IGrain
{
    [Alias(""stable-method"")]
    Task<NewResponse> NewMethod(NewRequest renamedParameter);
}
";
        var contractsFile = await ApplyCodeFixAndGetContractsAsync(
            oldSource,
            "# OrleansContracts.txt\n",
            GrainInterfaceVersionAnalyzer.RuleId0016);

        var diagnostics = await GetDiagnosticsAsync(newSource, contractsFile);

        Assert.Empty(diagnostics);
        Assert.Contains("# IOldGrain\ninterface [GrainInterfaceType(\"stable-interface\")] IOldGrain [Version(0)]", contractsFile);
        Assert.Contains(
            "  stable-method(request) -> Task<response>",
            contractsFile);
        Assert.Contains("# IOldGrain", contractsFile);
        Assert.Contains("# IOldGrain.OldMethod", contractsFile);
    }

    [Fact]
    public async Task StableInterfaceIdentityChange_IsBreaking()
    {
        const string source = @"
[GrainInterfaceType(""new-identity"")]
public interface IMyGrain : IGrain
{
}
";
        const string contractsFile = @"
[GrainInterfaceType(""old-identity"")] IMyGrain [Version(0)]
";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    [Fact]
    public async Task StableGrainTypeChange_IsBreaking()
    {
        const string source = @"
[GrainType(""new-identity"")]
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = "class [GrainType(\"old-identity\")] MyGrain";

        var diagnostics = await GetDiagnosticsAsync(source, contractsFile);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == GrainInterfaceVersionAnalyzer.RuleId0023);
    }

    #endregion

    #region Code Fix Tests Infrastructure

    private async Task<(Solution ChangedSolution, DocumentId? AdditionalDocumentId)> ApplyCodeFixAsync(
        string source,
        string? grainInterfacesFileContent,
        string expectedDiagnosticId)
    {
        var project = CreateProjectWithAdditionalFilesForCodeFix(source, grainInterfacesFileContent);
        var document = project.Documents.First();
        var compilation = await project.GetCompilationAsync();

        Assert.NotNull(compilation);
        var errors = compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(errors);

        var analyzer = new GrainInterfaceVersionAnalyzer();

        // Build analyzer options with additional files
        var additionalFiles = grainInterfacesFileContent is not null
            ? ImmutableArray.Create<AdditionalText>(new TestAdditionalText(OrleansContractsFileName, grainInterfacesFileContent))
            : ImmutableArray<AdditionalText>.Empty;

        var analyzerOptions = CreateAnalyzerOptions(additionalFiles, analyzerEnabled: true);

        var compilationWithAnalyzers = compilation
            .WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                analyzer.SupportedDiagnostics.ToDictionary(d => d.Id, d => ReportDiagnostic.Default)))
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer), analyzerOptions);

        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
        var diagnostic = diagnostics.FirstOrDefault(d => d.Id == expectedDiagnosticId);

        Assert.NotNull(diagnostic);

        // Apply code fix
        var codeFixer = new GrainInterfaceVersionCodeFix();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic!,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFixer.RegisterCodeFixesAsync(context);
        Assert.NotEmpty(actions);

        var operations = await actions.First().GetOperationsAsync(CancellationToken.None);
        var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;

        var additionalDocumentId = changedSolution.GetProject(project.Id)?.AdditionalDocumentIds.FirstOrDefault();

        return (changedSolution, additionalDocumentId);
    }

    private async Task<string> ApplyCodeFixAndGetContractsAsync(
        string source,
        string contractsFileContent,
        string expectedDiagnosticId)
    {
        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, contractsFileContent, expectedDiagnosticId);
        Assert.NotNull(additionalDocumentId);

        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        return (await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken)).ToString();
    }

    private static Project CreateProjectWithAdditionalFilesForCodeFix(string source, string? grainInterfacesFileContent)
    {
        const string fileName = "Test.cs";

        // Prepend usings
        var sb = new StringBuilder();
        foreach (var @using in Usings)
        {
            sb.AppendLine($"using {@using};");
        }
        sb.AppendLine(source);
        var fullSource = sb.ToString();

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

        var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "mscorlib.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Core.dll")));
        metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));

        var solution = new AdhocWorkspace()
            .CurrentSolution
            .AddProject(projectId, "TestProject", "TestProject", LanguageNames.CSharp)
            .AddMetadataReferences(projectId, metadataReferences)
            .AddDocument(documentId, fileName, SourceText.From(fullSource));

        // Add additional document if content is provided
        if (grainInterfacesFileContent is not null)
        {
            var additionalDocumentId = DocumentId.CreateNewId(projectId, OrleansContractsFileName);
            solution = solution.AddAdditionalDocument(additionalDocumentId, OrleansContractsFileName, SourceText.From(grainInterfacesFileContent));
        }

        return solution.GetProject(projectId)!
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    #endregion

    #region Code Fix Tests - Grain Classes

    [Fact]
    public async Task CodeFix_AddGrainClass()
    {
        const string source = @"
[GrainType(""my-grain"")]
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = @"# OrleansContracts.txt
IZulu [Version(1)]";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0022);

        Assert.Contains("class [GrainType(\"my-grain\")] MyGrain", content);
        Assert.True(
            content.IndexOf("IZulu [Version(1)]", StringComparison.Ordinal)
                < content.IndexOf("class [GrainType(\"my-grain\")] MyGrain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CodeFix_AddRecordGrainClass()
    {
        const string source = @"
[GrainType(""my-grain"")]
public record MyGrain : IGrainBase, IGrainWithStringKey
{
    public Orleans.Runtime.IGrainContext GrainContext => throw new NotImplementedException();
}
";
        const string contractsFile = "# OrleansContracts.txt\n";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0022);

        Assert.Contains("class [GrainType(\"my-grain\")] MyGrain", content);
    }

    [Fact]
    public async Task CodeFix_AddGrainClass_OmitsMatchingIdentityComment()
    {
        const string source = @"
[GrainType(""MyGrain"")]
public class MyGrain : Grain, IGrainWithStringKey
{
}
";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            "# OrleansContracts.txt\n",
            GrainInterfaceVersionAnalyzer.RuleId0022);

        Assert.Equal("class [GrainType(\"MyGrain\")] MyGrain\n", content);
    }

    [Fact]
    public async Task CodeFix_UpdateGrainClassAlias()
    {
        const string source = @"
[GrainType(""new-alias"")]
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = "class [GrainType(\"old-alias\")] MyGrain";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0023);

        Assert.Contains("class [GrainType(\"new-alias\")] MyGrain", content);
        Assert.DoesNotContain("old-alias", content);
    }

    [Fact]
    public async Task CodeFix_RetireGrainClass()
    {
        const string source = "public class SomeClass { }";
        const string contractsFile = "class [GrainType(\"my-grain\")] MyGrain";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0024);

        Assert.Contains("*RETIRED* class [GrainType(\"my-grain\")] MyGrain", content);
    }

    [Fact]
    public async Task CodeFix_AddGrainClass_ReactivatesRetiredDeclaration()
    {
        const string source = @"
[GrainType(""my-grain"")]
public class MyGrain : Grain, IGrainWithStringKey
{
}
";
        const string contractsFile = "*RETIRED* class [GrainType(\"old-alias\")] MyGrain";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0022);

        Assert.Equal("# MyGrain\nclass [GrainType(\"my-grain\")] MyGrain\n", content);
    }

    #endregion

    #region Code Fix Tests - ORLEANS0016 Add Interface

    [Fact]
    public async Task CodeFix_AddInterface_ToExistingFile()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
    static Task Utility() => Task.CompletedTask;
}
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
*RETIRED* SomeOther.IOldGrain [Version(1)]";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0016);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        // Should contain the new interface
        Assert.Contains("IMyGrain [Version(1)]", content);
        Assert.Contains("\n  DoSomething() -> Task", content);
        Assert.DoesNotContain("Utility", content);
    }

    [Fact]
    public async Task CodeFix_AddInterface_WithAlias()
    {
        const string source = @"
[Alias(""my-grain"")]
[Version(2)]
public interface IMyGrain : IGrain
{
    Task DoSomething(string name);
}
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
*RETIRED* SomeOther.IOldGrain [Version(1)]";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0016);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        Assert.DoesNotContain("[Alias(", content);
        Assert.Contains("IMyGrain [Version(2)]", content);
        Assert.Contains("\n  DoSomething(string) -> Task", content);
    }

    [Fact]
    public async Task CodeFix_AddInterface_SortsContractsAndMembers()
    {
        const string source = @"
[Version(1)]
public interface IMiddle : IGrain
{
    Task Zeta();
    Task Alpha();
}
";
        const string grainInterfacesFile = "# OrleansContracts.txt\n" +
            "IZulu [Version(1)]\n" +
            "IZulu.Method() -> Task\n\n" +
            "interface IAlpha [Version(1)]\n" +
            "IAlpha.Method() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0016);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        var alphaInterface = content.IndexOf("IAlpha [Version(1)]", StringComparison.Ordinal);
        var middleInterface = content.IndexOf("IMiddle [Version(1)]", StringComparison.Ordinal);
        var zuluInterface = content.IndexOf("IZulu [Version(1)]", StringComparison.Ordinal);
        var alphaMember = content.IndexOf("  Alpha() -> Task", StringComparison.Ordinal);
        var zetaMember = content.IndexOf("  Zeta() -> Task", StringComparison.Ordinal);

        Assert.True(alphaInterface < middleInterface);
        Assert.True(middleInterface < zuluInterface);
        Assert.True(alphaMember < zetaMember);
        Assert.Equal(
            "interface IAlpha [Version(1)]\n" +
            "  Method() -> Task\n\n" +
            "interface IMiddle [Version(1)]\n" +
            "  Alpha() -> Task\n" +
            "  Zeta() -> Task\n\n" +
            "interface IZulu [Version(1)]\n" +
            "  Method() -> Task\n",
            content);
    }

    [Fact]
    public async Task CodeFix_AddInterface_ProducesSameContentRegardlessOfApplicationOrder()
    {
        const string alphaSource = @"
[Version(1)]
public interface IAlpha : IGrain
{
    Task Method();
}
";
        const string zuluSource = @"
[Version(1)]
public interface IZulu : IGrain
{
    Task Method();
}
";

        var alphaThenZulu = await ApplyCodeFixAndGetContractsAsync(
            zuluSource,
            await ApplyCodeFixAndGetContractsAsync(
                alphaSource,
                "# OrleansContracts.txt\n",
                GrainInterfaceVersionAnalyzer.RuleId0016),
            GrainInterfaceVersionAnalyzer.RuleId0016);

        var zuluThenAlpha = await ApplyCodeFixAndGetContractsAsync(
            alphaSource,
            await ApplyCodeFixAndGetContractsAsync(
                zuluSource,
                "# OrleansContracts.txt\n",
                GrainInterfaceVersionAnalyzer.RuleId0016),
            GrainInterfaceVersionAnalyzer.RuleId0016);

        Assert.Equal(alphaThenZulu, zuluThenAlpha);
    }

    [Fact]
    public async Task CodeFix_AddInterface_ReactivatesByStableIdentity()
    {
        const string source = @"
[GrainInterfaceType(""stable-interface"")]
public interface INewGrain : IGrain
{
}
";
        const string contractsFile =
            "*RETIRED* [GrainInterfaceType(\"stable-interface\")] IOldGrain [Version(0)] # CLR: IOldGrain\n";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0016);

        Assert.Contains(
            "# INewGrain\ninterface [GrainInterfaceType(\"stable-interface\")] INewGrain [Version(0)]",
            content);
        Assert.DoesNotContain("*RETIRED*", content);
        Assert.Equal(1, content.Split(new[] { "stable-interface" }, StringSplitOptions.None).Length - 1);
    }

    #endregion

    #region Code Fix Tests - ORLEANS0017 Update Version

    [Fact]
    public async Task CodeFix_UpdateVersion()
    {
        const string source = @"
[Version(2)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0017);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        // Should have updated version
        Assert.Contains("IMyGrain [Version(2)]", content);
        Assert.DoesNotContain("[Version(1)]", content);
    }

    [Fact]
    public async Task CodeFix_UpdateVersion_UsesExactInterfaceName()
    {
        const string source = @"
[Version(2)]
public interface IFoo : IGrain
{
    Task DoSomething();
}

[Version(1)]
public interface IFooBar : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
IFooBar [Version(1)]
IFooBar.DoSomething() -> Task

IFoo [Version(1)]
IFoo.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0017);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        Assert.Contains("IFooBar [Version(1)]", content);
        Assert.Contains("IFoo [Version(2)]", content);
    }

    [Fact]
    public async Task CodeFix_UpdateVersion_PreservesLfLineEndings()
    {
        const string source = @"
[Version(2)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = "# OrleansContracts.txt\nIMyGrain [Version(1)]\nIMyGrain.DoSomething() -> Task\n";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0017);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        Assert.DoesNotContain("\r", content);
        Assert.Equal("interface IMyGrain [Version(2)]\n  DoSomething() -> Task\n", content);
    }

    #endregion

    #region Code Fix Tests - ORLEANS0018 Add Member

    [Fact]
    public async Task CodeFix_AddMember()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();
    Task NewMethod(int value);
}
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0018);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        // Should contain the new member
        Assert.Contains("\n  NewMethod(int) -> Task", content);
    }

    [Fact]
    public async Task CodeFix_AddMember_WithAlias()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task DoSomething();

    [Alias(""new-method"")]
    Task NewMethod(int value);
}
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0018);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        Assert.Contains("\n  new-method(int) -> Task", content);
        Assert.Contains("  # IMyGrain.NewMethod(int value) -> Task", content);
    }

    [Fact]
    public async Task CodeFix_AddMember_OmitsMatchingAliasComment()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    [Alias(""NewMethod"")]
    Task NewMethod();
}
";
        const string contractsFile = "IMyGrain [Version(1)]";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0018);

        Assert.Equal("interface IMyGrain [Version(1)]\n  NewMethod() -> Task\n", content);
    }

    [Fact]
    public async Task CodeFix_AddMember_IncludesGenericMethodArity()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrain
{
    Task<T> ReadStateAsync<T>(T value);
}
";
        const string contractsFile = "interface IMyGrain [Version(1)]";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0018);

        Assert.Equal(
            "interface IMyGrain [Version(1)]\n  ReadStateAsync`1(T) -> Task<T>\n",
            content);
    }

    [Fact]
    public async Task CodeFix_AddMember_InsertsBeforeNextInterface()
    {
        const string source = @"
[Version(1)]
public interface IFoo : IGrain
{
    Task NewMethod();
}

[Version(1)]
public interface IFooBar : IGrain
{
    Task ExistingMethod();
}
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
IFoo [Version(1)]
IFooBar [Version(1)]
IFooBar.ExistingMethod() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0018);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        Assert.True(
            content.IndexOf("  NewMethod() -> Task", StringComparison.Ordinal)
                < content.IndexOf("IFooBar [Version(1)]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CodeFix_AddMember_InsertsBeforeNextGrainClass()
    {
        const string source = @"
[Version(1)]
public interface IFoo : IGrain
{
    Task NewMethod();
}
";
        const string contractsFile = @"# OrleansContracts.txt
IFoo [Version(1)]
class SomeGrain";

        var content = await ApplyCodeFixAndGetContractsAsync(
            source,
            contractsFile,
            GrainInterfaceVersionAnalyzer.RuleId0018);

        Assert.True(
            content.IndexOf("  NewMethod() -> Task", StringComparison.Ordinal)
                < content.IndexOf("class SomeGrain", StringComparison.Ordinal));
    }

    #endregion

    #region Generic Grain Interfaces

    [Fact]
    public async Task GenericInterface_InFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain<T> : IGrain
{
    Task DoSomething(T value);
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain<T> [Version(1)]
IMyGrain<T>.DoSomething(T value) -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task GenericInterface_WithConstraint_InFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain<T> : IGrain where T : class
{
    Task DoSomething(T value);
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain<T> [Version(1)]
IMyGrain<T>.DoSomething(T value) -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    [Fact]
    public async Task GenericInterface_MultipleTypeParams_InFile_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain<TKey, TValue> : IGrain
{
    Task DoSomething(TKey key, TValue value);
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain<TKey, TValue> [Version(1)]
IMyGrain<TKey, TValue>.DoSomething(TKey key, TValue value) -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    #endregion

    #region Code Fix Tests - ORLEANS0019 Retire Interface

    [Fact]
    public async Task CodeFix_RetireInterface()
    {
        const string source = @"
// No grain interfaces - IOldGrain was removed
public class SomeClass { }
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
IOldGrain [Version(1)]
IOldGrain.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0019);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        // Should have *RETIRED* prefix
        Assert.Contains("*RETIRED* interface IOldGrain [Version(1)]", content);
    }

    [Fact]
    public async Task CodeFix_RetireInterface_UsesExactInterfaceName()
    {
        const string source = @"
public class SomeClass { }
";
        const string grainInterfacesFile = @"# OrleansContracts.txt
IFoo [Version(1)]
IFooBar [Version(1)]";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0019);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync(TestContext.Current.CancellationToken);
        var content = changedText.ToString();

        Assert.Contains("*RETIRED* interface IFoo [Version(1)]", content);
        Assert.Contains("\ninterface IFooBar [Version(1)]", content);
        Assert.DoesNotContain("*RETIRED* interface IFooBar", content);
    }

    #endregion

    #region Inherited Interfaces

    [Fact]
    public async Task InheritedInterface_BothTracked_NoDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IBaseGrain : IGrain
{
    Task DoBase();
}

[Version(1)]
public interface IDerivedGrain : IBaseGrain
{
    Task DoDerived();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IBaseGrain [Version(1)]
IBaseGrain.DoBase() -> Task

IDerivedGrain [Version(1)]
IDerivedGrain.DoDerived() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        // Both interfaces should be matched
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0017);
        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
    }

    [Fact]
    public async Task InheritedInterface_DerivedNotTracked_ReportsDiagnostic()
    {
        const string source = @"
[Version(1)]
public interface IBaseGrain : IGrain
{
    Task DoBase();
}

[Version(1)]
public interface IDerivedGrain : IBaseGrain
{
    Task DoDerived();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IBaseGrain [Version(1)]
IBaseGrain.DoBase() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        // IDerivedGrain should be reported as not declared
        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
        var diagnostic = diagnostics.First(d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
        Assert.Contains("IDerivedGrain", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InterfaceInheritingFromGrainWithKey_Tracked()
    {
        const string source = @"
[Version(1)]
public interface IMyGrain : IGrainWithStringKey
{
    Task DoSomething();
}
";
        const string grainInterfacesFile = @"
# OrleansContracts.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    #endregion
}
