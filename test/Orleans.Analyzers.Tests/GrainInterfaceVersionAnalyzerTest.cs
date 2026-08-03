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
    private const string GrainInterfacesFileName = "GrainInterfaces.txt";

    private static readonly string[] Usings = new[]
    {
        "System",
        "System.Threading.Tasks",
        "Orleans",
        "Orleans.CodeGeneration"
    };

    #region Test Infrastructure

    private async Task<Diagnostic[]> GetDiagnosticsAsync(string source, string? grainInterfacesFileContent = null)
    {
        var project = CreateProjectWithAdditionalFiles(source, grainInterfacesFileContent);
        var compilation = await project.GetCompilationAsync();
        var errors = compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);

        Assert.Empty(errors);

        var analyzer = new GrainInterfaceVersionAnalyzer();

        // Build analyzer options with additional files
        var additionalFiles = grainInterfacesFileContent is not null
            ? ImmutableArray.Create<AdditionalText>(new TestAdditionalText(GrainInterfacesFileName, grainInterfacesFileContent))
            : ImmutableArray<AdditionalText>.Empty;

        var analyzerOptions = new AnalyzerOptions(additionalFiles);

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

    #endregion

    #region ORLEANS0016 - Interface Not Declared

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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
[Alias(""my-grain"")] IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething(string name, int count) -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0018);
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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

        Assert.Contains(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0020);
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
            ? ImmutableArray.Create<AdditionalText>(new TestAdditionalText(GrainInterfacesFileName, grainInterfacesFileContent))
            : ImmutableArray<AdditionalText>.Empty;

        var analyzerOptions = new AnalyzerOptions(additionalFiles);

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
            var additionalDocumentId = DocumentId.CreateNewId(projectId, GrainInterfacesFileName);
            solution = solution.AddAdditionalDocument(additionalDocumentId, GrainInterfacesFileName, SourceText.From(grainInterfacesFileContent));
        }

        return solution.GetProject(projectId)!
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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
}
";
        const string grainInterfacesFile = @"# GrainInterfaces.txt
*RETIRED* SomeOther.IOldGrain [Version(1)]";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0016);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync();
        var content = changedText.ToString();

        // Should contain the new interface
        Assert.Contains("IMyGrain [Version(1)]", content);
        Assert.Contains("IMyGrain.DoSomething() -> Task", content);
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
        const string grainInterfacesFile = @"# GrainInterfaces.txt
*RETIRED* SomeOther.IOldGrain [Version(1)]";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0016);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync();
        var content = changedText.ToString();

        // Should contain the alias
        Assert.Contains("[Alias(\"my-grain\")]", content);
        Assert.Contains("IMyGrain [Version(2)]", content);
        Assert.Contains("IMyGrain.DoSomething(string name) -> Task", content);
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
        const string grainInterfacesFile = @"# GrainInterfaces.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0017);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync();
        var content = changedText.ToString();

        // Should have updated version
        Assert.Contains("IMyGrain [Version(2)]", content);
        Assert.DoesNotContain("[Version(1)]", content);
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
        const string grainInterfacesFile = @"# GrainInterfaces.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0018);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync();
        var content = changedText.ToString();

        // Should contain the new member
        Assert.Contains("IMyGrain.NewMethod(int value) -> Task", content);
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
        const string grainInterfacesFile = @"# GrainInterfaces.txt
IOldGrain [Version(1)]
IOldGrain.DoSomething() -> Task";

        var (changedSolution, additionalDocumentId) = await ApplyCodeFixAsync(source, grainInterfacesFile, GrainInterfaceVersionAnalyzer.RuleId0019);

        Assert.NotNull(additionalDocumentId);
        var changedDocument = changedSolution.GetAdditionalDocument(additionalDocumentId!);
        Assert.NotNull(changedDocument);

        var changedText = await changedDocument!.GetTextAsync();
        var content = changedText.ToString();

        // Should have *RETIRED* prefix
        Assert.Contains("*RETIRED* IOldGrain [Version(1)]", content);
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
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
# GrainInterfaces.txt
IMyGrain [Version(1)]
IMyGrain.DoSomething() -> Task
";
        var diagnostics = await GetDiagnosticsAsync(source, grainInterfacesFile);

        Assert.DoesNotContain(diagnostics, d => d.Id == GrainInterfaceVersionAnalyzer.RuleId0016);
    }

    #endregion
}
