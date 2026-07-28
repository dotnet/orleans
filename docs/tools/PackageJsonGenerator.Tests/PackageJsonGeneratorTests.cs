// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace PackageJsonGenerator.Tests;

public sealed class PackageJsonGeneratorTests
{
    [Fact]
    public void GeneratePackageJson_WritesEmptyTypesForAssemblyWithoutPublicApi()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            internal sealed class Implementation;
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net10.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Empty(document.RootElement.GetProperty("types").EnumerateArray());
    }

    [Fact]
    public void GeneratePackageJson_DoesNotEmitTypesFromReferenceAssemblies()
    {
        using var dependency = TestAssembly.Create(
            """
            namespace Dependency.Library;

            public sealed class DependencyType;
            """,
            assemblyName: "Dependency.Library");
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                public Dependency.Library.DependencyType Dependency { get; } = new();
            }
            """,
            additionalReferences: [dependency.AssemblyPath]);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net10.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToArray();

        Assert.Single(types);
        Assert.Equal("Sample.Library.Widget", types[0].GetProperty("fullName").GetString());
    }

    [Fact]
    public void GeneratePackageJson_UsesPortablePdbSourcePath()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                public void Run()
                {
                }
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            sourceRepoOverride: "https://github.com/dotnet/orleans.git",
            targetFrameworkOverride: "net10.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var type = document.RootElement.GetProperty("types").EnumerateArray().Single();

        Assert.Equal("https://github.com/dotnet/orleans", document.RootElement
            .GetProperty("package")
            .GetProperty("sourceRepository")
            .GetString());
        Assert.Equal("src/Sample.Library/Source0.cs", type.GetProperty("sourceFile").GetString());
        Assert.Equal("src/Sample.Library/Source0.cs", type
            .GetProperty("members")
            .EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "Run")
            .GetProperty("sourceFile")
            .GetString());
    }

    [Fact]
    public void GeneratePackageJson_ResolvesTypeDeclarationFromLocalSource()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                public void Run()
                {
                }
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");
        var sourceManifestPath = Path.Combine(assembly.DirectoryPath, "source-files.txt");
        File.WriteAllText(sourceManifestPath, "src/Sample.Library/Source0.cs");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            sourceRootOverride: assembly.DirectoryPath,
            sourceFileManifestOverride: sourceManifestPath,
            targetFrameworkOverride: "net10.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var type = document.RootElement.GetProperty("types").EnumerateArray().Single();

        Assert.Equal("3-3", type.GetProperty("sourceLines").GetString());
    }

    [Fact]
    public void GeneratePackageJson_OmitsUnverifiedInferredSourcePath()
    {
        using var assembly = TestAssembly.Create(
            [
                """
                namespace Sample.Library;

                public sealed class Anchor
                {
                    public void Run()
                    {
                    }
                }
                """,
                """
                namespace Sample.Library;

                public interface IWidget;
                """,
            ]);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");
        var sourceManifestPath = Path.Combine(assembly.DirectoryPath, "source-files.txt");
        File.WriteAllLines(
            sourceManifestPath,
            ["src/Sample.Library/Source0.cs", "src/Sample.Library/Source1.cs"]);

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            sourceRootOverride: assembly.DirectoryPath,
            sourceFileManifestOverride: sourceManifestPath,
            targetFrameworkOverride: "net10.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToArray();
        var anchor = types.Single(type => type.GetProperty("name").GetString() == "Anchor");
        var widget = types.Single(type => type.GetProperty("name").GetString() == "IWidget");

        Assert.Equal("src/Sample.Library/Source0.cs", anchor.GetProperty("sourceFile").GetString());
        Assert.False(widget.TryGetProperty("sourceFile", out _));
    }

    [Fact]
    public void GeneratePackageJson_WritesSelectedTargetFrameworkMetadata()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                public string Name => "demo";
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var package = document.RootElement.GetProperty("package");

        Assert.Equal("Sample.Package", package.GetProperty("name").GetString());
        Assert.Equal("1.2.3", package.GetProperty("version").GetString());
        Assert.Equal("net8.0", package.GetProperty("targetFramework").GetString());
    }

    [Fact]
    public void GeneratePackageJson_DoesNotMarkEnumsAsSealed()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public enum WidgetState
            {
                Unknown = 0,
                Ready = 1,
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var type = document.RootElement
            .GetProperty("types")
            .EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "WidgetState");

        Assert.Equal("enum", type.GetProperty("kind").GetString());
        Assert.False(type.TryGetProperty("isSealed", out _));
    }

    [Fact]
    public void GeneratePackageJson_PreservesPlainTextXmlListItems()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                /// <summary>Does work.</summary>
                /// <remarks>
                /// Happens when either:
                /// <list type="bullet">
                /// <item>The first condition is met.</item>
                /// <item><para>The second condition is met.</para></item>
                /// </list>
                /// </remarks>
                public void Run()
                {
                }
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var method = document.RootElement
            .GetProperty("types")
            .EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Widget")
            .GetProperty("members")
            .EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "Run");

        var remarks = method.GetProperty("docs").GetProperty("remarks").EnumerateArray().ToArray();
        var list = remarks.Single(node => node.GetProperty("kind").GetString() == "list");
        var items = list.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.Equal("The first condition is met.", items[0]
            .GetProperty("description")
            .EnumerateArray()
            .Single()
            .GetProperty("text")
            .GetString());

        var secondDescription = items[1]
            .GetProperty("description")
            .EnumerateArray()
            .Single();
        Assert.Equal("para", secondDescription.GetProperty("kind").GetString());
        Assert.Equal("The second condition is met.", secondDescription
            .GetProperty("children")
            .EnumerateArray()
            .Single()
            .GetProperty("text")
            .GetString());
    }

    [Fact]
    public void GeneratePackageJson_NormalizesToLfAndSkipsRewritingUnchangedOutput()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                public string Name => "demo";
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        var initialContent = File.ReadAllText(outputPath);
        Assert.DoesNotContain("\r", initialContent);

        File.WriteAllText(outputPath, initialContent.Replace("\n", "\r\n", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(outputPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var crlfWriteTime = File.GetLastWriteTimeUtc(outputPath);

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        var normalizedContent = File.ReadAllText(outputPath);
        Assert.DoesNotContain("\r", normalizedContent);
        Assert.NotEqual(crlfWriteTime, File.GetLastWriteTimeUtc(outputPath));

        File.SetLastWriteTimeUtc(outputPath, new DateTime(2001, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var unchangedWriteTime = File.GetLastWriteTimeUtc(outputPath);

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        Assert.Equal(unchangedWriteTime, File.GetLastWriteTimeUtc(outputPath));
    }

    [Fact]
    public void GeneratePackageJson_IncludesNestedTypesFromPartialDeclarations()
    {
        // Two partial declarations split across separate source files, mirroring
        // FoundryModel.cs + FoundryModel.Generated.cs. Roslyn merges partials, so
        // both nested types should be discoverable from the parent.
        using var assembly = TestAssembly.Create(
            [
                """
                namespace Sample.Library;

                public partial class Container
                {
                    public sealed class OpenAI
                    {
                        public string Name => "openai";
                    }
                }
                """,
                """
                namespace Sample.Library;

                public partial class Container
                {
                    public sealed class Anthropic
                    {
                        public string Name => "anthropic";
                    }
                }
                """,
            ]);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToList();

        var container = types.Single(t => t.GetProperty("fullName").GetString() == "Sample.Library.Container");
        var nested = container.GetProperty("nestedTypes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        Assert.Equal(
            ["Sample.Library.Container.Anthropic", "Sample.Library.Container.OpenAI"],
            nested);

        // Nested types should also be present as standalone type entries.
        Assert.Contains(types, t => t.GetProperty("fullName").GetString() == "Sample.Library.Container.OpenAI");
        Assert.Contains(types, t => t.GetProperty("fullName").GetString() == "Sample.Library.Container.Anthropic");
    }

    [Fact]
    public void GeneratePackageJson_OmitsNestedTypesArrayWhenNone()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                public string Name => "demo";
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");

        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net8.0");

        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        var widget = document.RootElement
            .GetProperty("types")
            .EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Widget");

        Assert.False(widget.TryGetProperty("nestedTypes", out _));
    }

    [Fact]
    public void GeneratePackageJson_ProducesIdenticalOrderedBytesForEquivalentApiSurface()
    {
        using var firstAssembly = TestAssembly.Create(
            """
            namespace Sample.Library
            {
                public sealed class Widget
                {
                    public string Name { get; } = "";
                    public void Run(string value) { }
                    public int Count;
                    public event System.Action Changed { add { } remove { } }
                    public void Run(int value) { }
                }
            }

            namespace Sample.Library
            {
                public sealed class Ångstrom;
            }
            """,
            debugInformationFormat: null);
        using var secondAssembly = TestAssembly.Create(
            """
            namespace Sample.Library
            {
                public sealed class Ångstrom;
            }

            namespace Sample.Library
            {
                public sealed class Widget
                {
                    public void Run(int value) { }
                    public event System.Action Changed { add { } remove { } }
                    public int Count;
                    public void Run(string value) { }
                    public string Name { get; } = "";
                }
            }
            """,
            debugInformationFormat: null);

        var firstOutputPath = Path.Combine(firstAssembly.DirectoryPath, "Package.json");
        var secondOutputPath = Path.Combine(secondAssembly.DirectoryPath, "Package.json");

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Generate(firstAssembly, firstOutputPath);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            Generate(secondAssembly, secondOutputPath);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        var firstBytes = File.ReadAllBytes(firstOutputPath);
        var secondBytes = File.ReadAllBytes(secondOutputPath);
        Assert.Equal(firstBytes, secondBytes);
        Assert.False(firstBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));

        using var document = JsonDocument.Parse(firstBytes);
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToArray();
        Assert.Equal(
            ["Sample.Library.Widget", "Sample.Library.Ångstrom"],
            types.Select(type => type.GetProperty("fullName").GetString()));

        var members = types[0].GetProperty("members").EnumerateArray().ToArray();
        Assert.Equal(
            ["constructor:.ctor", "event:Changed", "field:Count", "method:Run", "method:Run", "property:Name"],
            members.Select(member => $"{member.GetProperty("kind").GetString()}:{member.GetProperty("name").GetString()}"));
        Assert.Equal(
            ["public void Widget.Run(int value)", "public void Widget.Run(string value)"],
            members
                .Where(member => member.GetProperty("name").GetString() == "Run")
                .Select(member => member.GetProperty("signature").GetString()));
    }

    [Fact]
    public void GeneratePackageJson_EmitsOnlyPublicTypesAndMembersDeclaredByTheirOwningType()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public class Base
            {
                public void Inherited() { }
                protected void Protected() { }
                internal void Internal() { }
                private void Private() { }
            }

            public sealed class Derived : Base
            {
                public void Own() { }
                protected void ProtectedOwn() { }

                public sealed class VisibleNested
                {
                    public void NestedOwn() { }
                }

                internal sealed class HiddenNested;
                protected sealed class ProtectedNested;
                private sealed class PrivateNested;
            }

            internal sealed class Hidden
            {
                public void PublicOnInternalType() { }
                public sealed class PublicNestedOnInternalType;
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");
        Generate(assembly, outputPath);

        using var document = JsonDocument.Parse(File.ReadAllBytes(outputPath));
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToArray();

        Assert.Equal(
            [
                "Sample.Library.Base",
                "Sample.Library.Derived",
                "Sample.Library.Derived.VisibleNested",
            ],
            types.Select(type => type.GetProperty("fullName").GetString()));
        Assert.Equal([".ctor", "Inherited"], GetMemberNames(types, "Sample.Library.Base"));
        Assert.Equal([".ctor", "Own"], GetMemberNames(types, "Sample.Library.Derived"));
        Assert.Equal([".ctor", "NestedOwn"], GetMemberNames(types, "Sample.Library.Derived.VisibleNested"));
    }

    [Fact]
    public void GeneratePackageJson_ReadsSourceLocationsFromEmbeddedPortablePdb()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public sealed class Widget
            {
                public void Run()
                {
                }
            }
            """,
            debugInformationFormat: DebugInformationFormat.Embedded);
        Assert.False(File.Exists(Path.ChangeExtension(assembly.AssemblyPath, ".pdb")));

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");
        Generate(assembly, outputPath);

        using var document = JsonDocument.Parse(File.ReadAllBytes(outputPath));
        var type = document.RootElement.GetProperty("types").EnumerateArray().Single();
        var method = type.GetProperty("members")
            .EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "Run");

        Assert.Equal("src/Sample.Library/Source0.cs", type.GetProperty("sourceFile").GetString());
        Assert.Equal("src/Sample.Library/Source0.cs", method.GetProperty("sourceFile").GetString());
        Assert.Equal("6-6", type.GetProperty("sourceLines").GetString());
        Assert.Equal("6-7", method.GetProperty("sourceLines").GetString());
    }

    [Fact]
    public void GeneratePackageJson_DistinguishesGenericArityInSourceLocations()
    {
        using var assembly = TestAssembly.Create(
            [
                """
                namespace Sample.Library;

                public sealed class Executor
                {
                    public void Run()
                    {
                    }
                }
                """,
                """
                namespace Sample.Library;

                public sealed class Executor<T>
                {
                    public void Run(T value)
                    {
                    }
                }
                """,
            ]);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");
        Generate(assembly, outputPath);

        using var document = JsonDocument.Parse(File.ReadAllBytes(outputPath));
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToArray();
        var nonGeneric = types.Single(type => type.GetProperty("fullName").GetString() == "Sample.Library.Executor");
        var generic = types.Single(type => type.GetProperty("fullName").GetString() == "Sample.Library.Executor<T>");

        Assert.Equal("src/Sample.Library/Source0.cs", nonGeneric.GetProperty("sourceFile").GetString());
        Assert.Equal("src/Sample.Library/Source1.cs", generic.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void GeneratePackageJson_RemapsInheritedDocumentationNamesByOrdinal()
    {
        using var assembly = TestAssembly.Create(
            """
            namespace Sample.Library;

            public interface IWorker
            {
                /// <summary>Runs the worker.</summary>
                /// <typeparam name="TInput">The input type.</typeparam>
                /// <param name="input">The input value.</param>
                void Run<TInput>(TInput input);
            }

            public sealed class Worker : IWorker
            {
                /// <inheritdoc/>
                public void Run<TValue>(TValue value)
                {
                }
            }
            """);

        var outputPath = Path.Combine(assembly.DirectoryPath, "Package.json");
        Generate(assembly, outputPath);

        using var document = JsonDocument.Parse(File.ReadAllBytes(outputPath));
        var method = document.RootElement
            .GetProperty("types")
            .EnumerateArray()
            .Single(type => type.GetProperty("fullName").GetString() == "Sample.Library.Worker")
            .GetProperty("members")
            .EnumerateArray()
            .Single(member => member.GetProperty("name").GetString() == "Run");
        var docs = method.GetProperty("docs");

        Assert.True(docs.GetProperty("parameters").TryGetProperty("value", out _));
        Assert.False(docs.GetProperty("parameters").TryGetProperty("input", out _));
        Assert.True(docs.GetProperty("typeParameters").TryGetProperty("TValue", out _));
        Assert.False(docs.GetProperty("typeParameters").TryGetProperty("TInput", out _));
    }

    [Theory]
    [InlineData("inputAssembly", true, "Input assembly path is required.")]
    [InlineData("inputAssembly", false, "Input assembly path is required.")]
    [InlineData("references", true, "At least one reference assembly is required.")]
    [InlineData("references", false, "At least one reference assembly is required.")]
    [InlineData("outputFile", true, "Output file path is required.")]
    [InlineData("outputFile", false, "Output file path is required.")]
    public void GeneratePackageJson_RejectsMissingRequiredArguments(
        string parameterName,
        bool useNull,
        string expectedMessage)
    {
        string? inputAssembly = "input.dll";
        string[]? references = ["reference.dll"];
        string? outputFile = "output.json";

        switch (parameterName)
        {
            case "inputAssembly":
                inputAssembly = useNull ? null : "";
                break;
            case "references":
                references = useNull ? null : [];
                break;
            case "outputFile":
                outputFile = useNull ? null : "";
                break;
        }

        var exception = Assert.Throws<ArgumentException>(() =>
            PackageJsonGenerator.GeneratePackageJson(inputAssembly, references, outputFile));

        Assert.Equal(parameterName, exception.ParamName);
        Assert.StartsWith(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    private static void Generate(TestAssembly assembly, string outputPath)
    {
        PackageJsonGenerator.GeneratePackageJson(
            assembly.AssemblyPath,
            assembly.References,
            outputPath,
            versionOverride: "1.2.3",
            packageNameOverride: "Sample.Package",
            targetFrameworkOverride: "net10.0");
    }

    private static IEnumerable<string?> GetMemberNames(JsonElement[] types, string fullName) =>
        types.Single(type => type.GetProperty("fullName").GetString() == fullName)
            .GetProperty("members")
            .EnumerateArray()
            .Select(member => member.GetProperty("name").GetString());

    private sealed class TestAssembly : IDisposable
    {
        private TestAssembly(string directoryPath, string assemblyPath, string[] references)
        {
            DirectoryPath = directoryPath;
            AssemblyPath = assemblyPath;
            References = references;
        }

        public string DirectoryPath { get; }

        public string AssemblyPath { get; }

        public string[] References { get; }

        public static TestAssembly Create(
            string source,
            string assemblyName = "Sample.Library",
            string[]? additionalReferences = null,
            DebugInformationFormat? debugInformationFormat = DebugInformationFormat.PortablePdb) =>
            Create([source], assemblyName, additionalReferences, debugInformationFormat);

        public static TestAssembly Create(
            string[] sources,
            string assemblyName = "Sample.Library",
            string[]? additionalReferences = null,
            DebugInformationFormat? debugInformationFormat = DebugInformationFormat.PortablePdb)
        {
            var tempDirectory = Directory.CreateTempSubdirectory("pkg-generator-tests-");
            var assemblyPath = Path.Combine(tempDirectory.FullName, $"{assemblyName}.dll");
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            var xmlPath = Path.ChangeExtension(assemblyPath, ".xml");
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "src", assemblyName));

            var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                ?? throw new InvalidOperationException("Trusted platform assemblies were not available.");

            var references = trustedPlatformAssemblies
                .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Concat(additionalReferences ?? [])
                .ToArray();

            var compilation = CSharpCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: sources.Select((source, index) =>
                {
                    var sourcePath = Path.Combine(sourceDirectory.FullName, $"Source{index}.cs");
                    File.WriteAllText(sourcePath, source, Encoding.UTF8);
                    return CSharpSyntaxTree.ParseText(
                        SourceText.From(source, Encoding.UTF8),
                        path: $"/_/src/{assemblyName}/Source{index}.cs");
                }),
                references: references.Select(reference => MetadataReference.CreateFromFile(reference)),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var assemblyStream = File.Create(assemblyPath);
            using var pdbStream = debugInformationFormat == DebugInformationFormat.PortablePdb
                ? File.Create(pdbPath)
                : null;
            using var xmlStream = File.Create(xmlPath);

            var emitResult = compilation.Emit(
                peStream: assemblyStream,
                pdbStream: pdbStream,
                xmlDocumentationStream: xmlStream,
                options: debugInformationFormat is { } format
                    ? new EmitOptions(debugInformationFormat: format)
                    : null);

            Assert.True(
                emitResult.Success,
                string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString())));

            return new TestAssembly(tempDirectory.FullName, assemblyPath, references);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
