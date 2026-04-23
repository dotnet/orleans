using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Model.Incremental;
using Orleans.Serialization;
using Xunit;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests that <see cref="ModelExtractor"/> correctly extracts value models from source declarations.
/// Validates that extracted models capture all necessary data for incremental pipeline caching
/// and that identical inputs produce equal models.
/// </summary>
public class ModelExtractorTests
{
    [Fact]
    public async Task ExtractSerializableTypeModel_BasicClass_CapturesCorrectData()
    {
        var code = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;

    [Id(1)]
    public int Count { get; set; }
}";
        var (model, _) = await ExtractFirstSerializableType(code);

        Assert.Equal("DemoData", model.Name);
        Assert.Equal("TestProject", model.Namespace);
        Assert.Equal(Accessibility.Public, model.Accessibility);
        Assert.False(model.IsValueType);
        Assert.False(model.IsEnumType);
        Assert.False(model.IsAbstractType);
        Assert.False(model.IsGenericType);
        Assert.Equal(2, model.Members.Count);

        // Auto-properties are stored as backing fields; match via BackingPropertyName
        var valueMember = Assert.Single(model.Members, m => m.BackingPropertyName == "Value");
        var countMember = Assert.Single(model.Members, m => m.BackingPropertyName == "Count");
        Assert.Equal((uint)0, valueMember.FieldId);
        Assert.Equal((uint)1, countMember.FieldId);
    }

    [Fact]
    public async Task ExtractSerializableTypeModel_ValueType_CapturesCorrectFlags()
    {
        var code = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public struct DemoStruct
{
    [Id(0)]
    public int X { get; set; }
}";
        var (model, _) = await ExtractFirstSerializableType(code);

        Assert.True(model.IsValueType);
        Assert.Equal("DemoStruct", model.Name);
        Assert.Equal(ObjectCreationStrategy.Default, model.CreationStrategy);
    }

    [Fact]
    public async Task ExtractSerializableTypeModel_GenericType_CapturesTypeParameters()
    {
        var code = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public class GenericData<T>
{
    [Id(0)]
    public T Value { get; set; }
}";
        var (model, _) = await ExtractFirstSerializableType(code);

        Assert.True(model.IsGenericType);
        Assert.Single(model.TypeParameters);
        Assert.Equal("T", model.TypeParameters[0].Name);
    }

    [Fact]
    public async Task ExtractSerializableTypeModel_SameInput_ProducesEqualModels()
    {
        var code = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;

    [Id(1)]
    public int Count { get; set; }
}";
        var (model1, compilation) = await ExtractFirstSerializableType(code);

        // Extract again from the same compilation — should produce identical model
        var model2 = ExtractFromCompilation(compilation);

        Assert.Equal(model1, model2);
        Assert.Equal(model1.GetHashCode(), model2.GetHashCode());
    }

    [Fact]
    public async Task ExtractSerializableTypeModel_DifferentInput_ProducesUnequalModels()
    {
        var code1 = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}";
        var code2 = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;

    [Id(1)]
    public int NewField { get; set; }
}";
        var (model1, _) = await ExtractFirstSerializableType(code1);
        var (model2, _) = await ExtractFirstSerializableType(code2);

        Assert.NotEqual(model1, model2);
    }

    [Fact]
    public async Task ExtractSerializableTypeModel_Enum_CapturesEnumFlag()
    {
        var code = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public enum DemoEnum
{
    A,
    B,
    C
}";
        var (model, _) = await ExtractFirstSerializableType(code);

        Assert.True(model.IsEnumType);
        Assert.True(model.IsValueType);
        Assert.Equal("DemoEnum", model.Name);
    }

    [Fact]
    public async Task ExtractSerializableTypeModel_SealedClass_CapturesSealedFlag()
    {
        var code = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public sealed class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}";
        var (model, _) = await ExtractFirstSerializableType(code);
        Assert.True(model.IsSealedType);
    }

    [Fact]
    public async Task ExtractSerializableTypeModel_Record_IncludesPrimaryConstructorParameters()
    {
        var code = @"
using Orleans;
namespace TestProject;

[GenerateSerializer]
public record DemoRecord([property: Id(0)] string Value, [property: Id(1)] int Count);
";
        var (model, _) = await ExtractFirstSerializableType(code);

        Assert.Equal("DemoRecord", model.Name);
        Assert.True(model.IncludePrimaryConstructorParameters);
    }

    [Fact]
    public async Task FieldIdAssignmentHelper_TypeWithComputedPropertyAndNoIds_RemainsValidWithoutSerializableCandidates()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class ComputedDto
            {
                public string Value => string.Empty;
            }
            """;
        var compilation = await CreateCompilation(code);
        var helper = CreateFieldIdAssignmentHelper(compilation, "TestProject.ComputedDto");

        Assert.True(helper.IsValidForSerialization);
        Assert.Null(helper.FailureReason);
        Assert.DoesNotContain(helper.Members, member => helper.TryGetSymbolKey(member, out _));
    }

    [Fact]
    public async Task ExtractFromAttributeContext_CanceledToken_ThrowsOperationCanceledException()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public sealed class DemoData
            {
                [Id(0)]
                public string Value { get; set; } = string.Empty;
            }
            """;
        var compilation = await CreateCompilation(code);
        var context = CreateSerializableAttributeContext(compilation, "DemoData");
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => ModelExtractor.ExtractFromAttributeContext(context, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task ExtractReferenceAssemblyData_CollectsCrossAssemblyMetadataAndDeterministicOrdering()
    {
        var consumerCompilation = await CreateReferenceExtractionCompilation();
        var model = ModelExtractor.ExtractReferenceAssemblyData(consumerCompilation, default);

        Assert.Equal(
            model.ApplicationParts.Select(static part => part.Value).OrderBy(static part => part, StringComparer.Ordinal),
            model.ApplicationParts.Select(static part => part.Value));
        Assert.Contains("ConsumerProject", model.ApplicationParts.Select(static part => part.Value));
        Assert.Contains("LibraryA", model.ApplicationParts.Select(static part => part.Value));
        Assert.Contains("Alpha.Part", model.ApplicationParts.Select(static part => part.Value));
        Assert.Contains("Zeta.Part", model.ApplicationParts.Select(static part => part.Value));

        Assert.Contains(model.WellKnownTypeIds, entry => entry.Type.SyntaxString == "global::LibraryA.AlphaType" && entry.Id == 100u);
        Assert.Contains(model.WellKnownTypeIds, entry => entry.Type.SyntaxString == "global::LibraryB.BetaType" && entry.Id == 200u);

        Assert.Contains(model.TypeAliases, entry => entry.Type.SyntaxString == "global::LibraryA.AlphaType" && entry.Alias == "A.Alias");
        Assert.Contains(model.TypeAliases, entry => entry.Type.SyntaxString == "global::LibraryB.BetaType" && entry.Alias == "B.Alias");

        var compoundAlias = Assert.Single(model.CompoundTypeAliases, entry => entry.TargetType.SyntaxString == "global::LibraryB.BetaType");
        Assert.Equal(2, compoundAlias.Components.Count);
        Assert.True(compoundAlias.Components[0].IsString);
        Assert.Equal("B", compoundAlias.Components[0].StringValue);
        Assert.True(compoundAlias.Components[1].IsType);
        Assert.Equal("global::LibraryB.BetaType", compoundAlias.Components[1].TypeValue.SyntaxString);

        var registeredCodecTypes = model.RegisteredCodecs.Select(static codec => codec.Type.SyntaxString).ToArray();
        Assert.Equal(
            registeredCodecTypes.OrderBy(static name => name, StringComparer.Ordinal),
            registeredCodecTypes);
        Assert.Contains("global::LibraryB.ActivatorType", registeredCodecTypes);
        Assert.Contains("global::LibraryB.CopierType", registeredCodecTypes);
        Assert.Contains("global::LibraryB.ConverterType", registeredCodecTypes);
        Assert.Contains("global::LibraryB.SerializerType", registeredCodecTypes);
        Assert.Contains(model.InterfaceImplementations, implementation => implementation.ImplementationType.SyntaxString == "global::LibraryB.GeneratedInterfaceImplementation");
    }

    [Fact]
    public async Task ExtractReferenceAssemblyData_IsStableWhenReferenceOrderChanges()
    {
        var compilationA = await CreateReferenceExtractionCompilation();
        var compilationB = await CreateReferenceExtractionCompilation(reverseReferenceOrder: true);

        var modelA = ModelExtractor.ExtractReferenceAssemblyData(compilationA, default);
        var modelB = ModelExtractor.ExtractReferenceAssemblyData(compilationB, default);

        Assert.Equal(modelA, modelB);
        Assert.Equal(modelA.GetHashCode(), modelB.GetHashCode());
    }

    [Fact]
    public async Task ExtractProxyInterfaceModel_InheritedGenerateMethodSerializers_FallsBackToInheritedAttribute()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IBaseGrain
            {
                ValueTask Ping();
            }

            public interface IDerivedGrain : IBaseGrain
            {
            }
            """;
        var compilation = await CreateCompilation(code);
        var model = ExtractProxyInterfaceModel(compilation, "TestProject.IDerivedGrain");

        Assert.Equal("IDerivedGrain", model.Name);
        Assert.Single(model.Methods, method => method.Name == "Ping");
    }

    [Fact]
    public async Task ExtractProxyInterfaceModel_ProxyBase_IncludesInvokableBaseMappings()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain
            {
                ValueTask Ping();
            }
            """;
        var compilation = await CreateCompilation(code);
        var model = ExtractProxyInterfaceModel(compilation, "TestProject.ITestGrain");

        Assert.NotEmpty(model.ProxyBase.InvokableBaseTypes);
        Assert.Contains(
            model.ProxyBase.InvokableBaseTypes,
            mapping => mapping.ReturnType.SyntaxString.Contains("ValueTask", StringComparison.Ordinal)
                && mapping.InvokableBaseType.SyntaxString.Contains("Request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractProxyInterfaceModel_BaseInterfaceOrder_DoesNotAffectMethodOrdering()
    {
        const string code1 = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IFirst
            {
                ValueTask First();
            }

            public interface ISecond
            {
                ValueTask Second();
            }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : IFirst, ISecond
            {
            }
            """;
        const string code2 = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IFirst
            {
                ValueTask First();
            }

            public interface ISecond
            {
                ValueTask Second();
            }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : ISecond, IFirst
            {
            }
            """;

        var model1 = ExtractProxyInterfaceModel(await CreateCompilation(code1), "TestProject.ITestGrain");
        var model2 = ExtractProxyInterfaceModel(await CreateCompilation(code2), "TestProject.ITestGrain");

        Assert.Equal(model1.Methods.Select(method => method.Name), model2.Methods.Select(method => method.Name));
    }

    [Fact]
    public async Task ExtractProxyInterfaceModel_UsesOriginalMethodDefinitionForGeneratedMethodId()
    {
        const string code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IBaseGrain<T>
            {
                ValueTask<T> Echo(T value);
            }

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface ITestGrain : IBaseGrain<int>
            {
            }
            """;
        var compilation = await CreateCompilation(code);
        var model = ExtractProxyInterfaceModel(compilation, "TestProject.ITestGrain");

        var method = Assert.Single(model.Methods);
        var baseInterface = compilation.GetTypeByMetadataName("TestProject.IBaseGrain`1");
        Assert.NotNull(baseInterface);
        var originalMethod = Assert.Single(baseInterface.GetMembers("Echo").OfType<IMethodSymbol>());
        var expectedMethodId = CodeGenerator.CreateHashedMethodId(originalMethod);

        Assert.Equal(expectedMethodId, method.GeneratedMethodId);
    }

    #region Helpers

    private static async Task<CSharpCompilation> CreateReferenceExtractionCompilation(bool reverseReferenceOrder = false)
    {
        const string libraryBCode = """
            using Orleans;
            using System.Threading.Tasks;

            namespace LibraryB;

            [Id(200)]
            [Alias("B.Alias")]
            [CompoundTypeAlias("B", typeof(LibraryB.BetaType))]
            public sealed class BetaType
            {
            }

            [RegisterSerializer]
            public sealed class SerializerType
            {
            }

            [RegisterCopier]
            public sealed class CopierType
            {
            }

            [RegisterActivator]
            public sealed class ActivatorType
            {
            }

            [RegisterConverter]
            public sealed class ConverterType
            {
            }

            [GenerateMethodSerializers(typeof(object))]
            public interface IGeneratedInterface
            {
                Task Ping();
            }

            public sealed class GeneratedInterfaceImplementation : IGeneratedInterface
            {
                public Task Ping() => Task.CompletedTask;
            }
            """;

        const string libraryACode = """
            using Orleans;
            using LibraryB;

            [assembly: ApplicationPart("Zeta.Part")]
            [assembly: ApplicationPart("Alpha.Part")]
            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryB.BetaType))]

            namespace LibraryA;

            [Id(100)]
            [Alias("A.Alias")]
            public sealed class AlphaType
            {
            }
            """;

        const string consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryA.AlphaType))]

            namespace ConsumerProject;

            public sealed class ConsumerMarker
            {
            }
            """;

        var libraryBCompilation = await CreateCompilation(libraryBCode, "LibraryB");
        Assert.Empty(libraryBCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var libraryACompilation = await CreateCompilation(
            libraryACode,
            "LibraryA",
            libraryBCompilation.ToMetadataReference());
        Assert.Empty(libraryACompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var libraryAReference = libraryACompilation.ToMetadataReference();
        var libraryBReference = libraryBCompilation.ToMetadataReference();
        var consumerCompilation = reverseReferenceOrder
            ? await CreateCompilation(consumerCode, "ConsumerProject", libraryBReference, libraryAReference)
            : await CreateCompilation(consumerCode, "ConsumerProject", libraryAReference, libraryBReference);

        Assert.Empty(consumerCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return consumerCompilation;
    }

    private static async Task<(SerializableTypeModel Model, CSharpCompilation Compilation)> ExtractFirstSerializableType(string code)
    {
        var compilation = await CreateCompilation(code);
        var model = ExtractFromCompilation(compilation);
        return (model, compilation);
    }

    private static SerializableTypeModel ExtractFromCompilation(CSharpCompilation compilation)
    {
        var context = CreateFirstSerializableAttributeContext(compilation);
        return ModelExtractor.ExtractFromAttributeContext(context, default);
    }

    private static FieldIdAssignmentHelper CreateFieldIdAssignmentHelper(
        CSharpCompilation compilation,
        string metadataName,
        Orleans.CodeGenerator.Model.GenerateFieldIds generateFieldIds = Orleans.CodeGenerator.Model.GenerateFieldIds.None)
    {
        var typeSymbol = compilation.GetTypeByMetadataName(metadataName);
        Assert.NotNull(typeSymbol);

        var options = new CodeGeneratorOptions
        {
            GenerateFieldIds = generateFieldIds,
        };
        var libraryTypes = LibraryTypes.FromCompilation(compilation, options);
        return new FieldIdAssignmentHelper(typeSymbol, ImmutableArray<IParameterSymbol>.Empty, generateFieldIds, libraryTypes);
    }

    private static GeneratorAttributeSyntaxContext CreateSerializableAttributeContext(CSharpCompilation compilation, string typeName)
    {
        var syntaxTree = Assert.Single(compilation.SyntaxTrees);
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var typeDeclaration = syntaxTree.GetRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == typeName);
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
        Assert.NotNull(typeSymbol);

        var generateSerializerAttribute = compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute");
        Assert.NotNull(generateSerializerAttribute);

        var attributes = typeSymbol.GetAttributes()
            .Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generateSerializerAttribute))
            .ToImmutableArray();
        Assert.Single(attributes);

        var constructor = typeof(GeneratorAttributeSyntaxContext).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(SyntaxNode), typeof(ISymbol), typeof(SemanticModel), typeof(ImmutableArray<AttributeData>)],
            modifiers: null);
        Assert.NotNull(constructor);

        return (GeneratorAttributeSyntaxContext)constructor.Invoke([typeDeclaration, typeSymbol, semanticModel, attributes]);
    }

    private static GeneratorAttributeSyntaxContext CreateFirstSerializableAttributeContext(CSharpCompilation compilation)
    {
        var syntaxTree = Assert.Single(compilation.SyntaxTrees);
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var generateSerializerAttribute = compilation.GetTypeByMetadataName("Orleans.GenerateSerializerAttribute");
        Assert.NotNull(generateSerializerAttribute);

        foreach (var declaration in syntaxTree.GetRoot().DescendantNodes())
        {
            ISymbol? symbol = declaration switch
            {
                TypeDeclarationSyntax typeDeclaration => semanticModel.GetDeclaredSymbol(typeDeclaration),
                EnumDeclarationSyntax enumDeclaration => semanticModel.GetDeclaredSymbol(enumDeclaration),
                _ => null,
            };

            if (symbol is null)
            {
                continue;
            }

            var attributes = symbol.GetAttributes()
                .Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generateSerializerAttribute))
                .ToImmutableArray();
            if (attributes.Length == 0)
            {
                continue;
            }

            var constructor = typeof(GeneratorAttributeSyntaxContext).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                [typeof(SyntaxNode), typeof(ISymbol), typeof(SemanticModel), typeof(ImmutableArray<AttributeData>)],
                modifiers: null);
            Assert.NotNull(constructor);

            return (GeneratorAttributeSyntaxContext)constructor.Invoke([declaration, symbol, semanticModel, attributes]);
        }

        throw new InvalidOperationException("No [GenerateSerializer] declaration was found.");
    }

    private static ProxyInterfaceModel ExtractProxyInterfaceModel(CSharpCompilation compilation, string metadataName)
    {
        var interfaceType = compilation.GetTypeByMetadataName(metadataName);
        Assert.NotNull(interfaceType);

        var model = ModelExtractor.ExtractProxyInterfaceModel(interfaceType, compilation, default);
        Assert.NotNull(model);
        return model;
    }

    private static Task<CSharpCompilation> CreateCompilation(
        string sourceCode,
        string assemblyName = "TestProject",
        params MetadataReference[] additionalReferences)
        => TestCompilationHelper.CreateCompilation(sourceCode, assemblyName, additionalReferences);

    #endregion
}
