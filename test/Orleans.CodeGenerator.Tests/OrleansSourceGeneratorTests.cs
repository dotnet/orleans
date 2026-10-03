using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.Serialization;

namespace Orleans.CodeGenerator.Tests;

/// <summary>
/// Tests for the Orleans source generator that generates serialization and RPC code.
///
/// The Orleans source generator uses Roslyn source generators to:
/// - Generate serializers for types marked with [GenerateSerializer]
/// - Generate proxy classes for grain interfaces
/// - Generate invokable wrappers for grain methods
/// - Generate metadata for Orleans runtime
///
/// Key features tested:
/// - Serialization code generation for various type patterns
/// - Support for different C# language features (records, nullable reference types, etc.)
/// - Grain proxy generation for different grain key types
/// - Proper handling of generic types
/// - Diagnostics for incorrect usage
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("CodeGen")]
public class OrleansSourceGeneratorTests
{
    /// <summary>
    /// Tests basic serializer generation for a simple class with a string property.
    /// This is the most common scenario - a POCO with properties marked with [Id] attributes.
    /// </summary>
    [Fact]
    public Task TestBasicClass() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}");

    /// <summary>
    /// Tests serializer generation for classes with private readonly fields.
    /// Verifies that the generator can handle:
    /// - Private fields (not just public properties)
    /// - Readonly fields that must be set via constructor
    /// - Property getters that expose private field values
    /// </summary>
    [Fact]
    public Task TestBasicClassWithoutNamespace() => AssertSuccessfulSourceGeneration(
@"using Orleans;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}");

    [Fact]
    public Task TestBasicClassWithDifferentAccessModifiers() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class PublicDemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}

[GenerateSerializer]
internal class InternalDemoData
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}");

    /// <summary>
    /// Tests serializer generation for classes with private readonly fields.
    /// Verifies that the generator can handle:
    /// - Private fields (not just public properties)
    /// - Readonly fields that must be set via constructor
    /// - Property getters that expose private field values
    /// </summary>
    [Fact]
    public Task TestBasicClassWithAnnotatedFields() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class DemoDataWithFields
{
    [Id(0)]
    private readonly int _intValue;

    [Id(1)]
    private readonly string _stringValue;

    public DemoDataWithFields(int intValue, string stringValue)
    {
        _intValue = intValue;
        _stringValue = stringValue;
    }

    public int IntValue => _intValue;

    public string StringValue => _stringValue;
}");

    [Fact]
    public Task TestBasicClassWithInheritance() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public abstract class BaseData
{
    [Id(0)]
    public string BaseValue { get; set; } = string.Empty;

    protected BaseData(string value)
    {
        BaseValue = value;
    }
}

[GenerateSerializer]
public class DerivedData : BaseData
{
    [Id(1)]
    public string DerivedValue { get; set; } = string.Empty;

    public DerivedData(string baseValue, string derivedValue) : base(baseValue)
    {
        DerivedValue = derivedValue;
    }
}");

    /// <summary>
    /// Tests serializer generation for value types (structs).
    /// Structs have different semantics than classes (value vs reference types)
    /// and the generator must handle them appropriately.
    /// </summary>
    [Fact]
    public Task TestBasicStruct() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public struct DemoData
{
    [Id(0)]
    public string Value { get; set; }
}");

    /// <summary>
    /// Tests serializer generation for C# 9+ record types.
    /// Records are immutable by default and use positional parameters,
    /// requiring special handling for:
    /// - Record structs vs record classes
    /// - Property attributes on positional parameters
    /// - Init-only properties
    /// </summary>
    [Fact]
    public Task TestRecords() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public record struct DemoDataRecordStruct([property: Id(0)] string Value);

[GenerateSerializer]
public record class DemoDataRecordClass([property: Id(0)] string Value);

[GenerateSerializer]
public record DemoDataRecord([property: Id(0)] string Value);");

    /// <summary>
    /// Tests serializer generation for records with [Id] attributes on primary constructor parameters.
    /// </summary>
    [Fact]
    public Task TestRecordsWithParameterIdAttributes() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public record SimpleRecord([Id(10)] int Value, [Id(20)] string Name);

[GenerateSerializer]
public record RecordWithExtraProperty([Id(30)] int Id, [Id(40)] string Name)
{
    [Id(50)]
    public string Description { get; init; }
}

[GenerateSerializer]
public record struct RecordStructWithParameterId([Id(60)] string Value);");

    /// <summary>
    /// Tests serializer generation for generic types.
    /// Generic types require:
    /// - Generating specialized serializers for each concrete type usage
    /// - Handling type parameters in serialization logic
    /// - Supporting nested generic types
    /// </summary>
    [Fact]
    public Task TestGenericClass() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class GenericData<T>
{
    [Id(0)]
    public T Value { get; set; }

    [Id(1)]
    public string Description { get; set; } = string.Empty;
}

// Also need a concrete usage to trigger generation for a specific type
[GenerateSerializer]
public class ConcreteUsage
{
    [Id(0)]
    public GenericData<int> IntData { get; set; }

    [Id(1)]
    public GenericData<string> StringData { get; set; }
}");

    /// <summary>
    /// Tests serializer generation for classes with nullable reference types.
    /// Verifies support for C# 8+ nullable reference types including:
    /// - Nullable and non-nullable reference properties
    /// - Required properties (C# 11+)
    /// - Init-only setters
    /// </summary>
    [Fact]
    public Task TestClassReferenceProperties() => AssertSuccessfulSourceGeneration(
@"#nullable enable
using Orleans;

namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public string? NullableStringProp { get; set; }

    [Id(1)]
    public string StringProp { get; set; } = string.Empty;

    [Id(2)]
    public required string RequiredStringProp { get; set; }

    [Id(3)]
    public required string RequiredStringPropInitOnly { get; init; }
}");

    /// <summary>
    /// Tests serializer generation for all primitive .NET types.
    /// Ensures the generator has built-in support for:
    /// - Numeric types (int, long, float, double, decimal, etc.)
    /// - Boolean, char, string
    /// - Date/time types (DateTime, DateTimeOffset, TimeSpan)
    /// - GUID and arrays
    /// </summary>
    [Fact]
    public Task TestClassPrimitiveTypes() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using System;

namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public int IntProp { get; set; }

    [Id(1)]
    public double DoubleProp { get; set; }

    [Id(2)]
    public float FloatProp { get; set; }

    [Id(3)]
    public long LongProp { get; set; }

    [Id(4)]
    public bool BoolProp { get; set; }

    [Id(5)]
    public byte ByteProp { get; set; }

    [Id(6)]
    public short ShortProp { get; set; }

    [Id(7)]
    public char CharProp { get; set; }

    [Id(8)]
    public uint UIntProp { get; set; }

    [Id(9)]
    public ulong ULongProp { get; set; }

    [Id(10)]
    public ushort UShortProp { get; set; }

    [Id(11)]
    public sbyte SByteProp { get; set; }

    [Id(12)]
    public decimal DecimalProp { get; set; }

    [Id(13)]
    public DateTime DateTimeProp { get; set; }

    [Id(14)]
    public DateTimeOffset DateTimeOffsetProp { get; set; }

    [Id(15)]
    public TimeSpan TimeSpanProp { get; set; }

    [Id(16)]
    public Guid GuidProp { get; set; }

    [Id(17)]
    public int[] IntArrayProp { get; set; }
}");

    [Fact]
    public Task TestClassPrimitiveTypesUsingFullName() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public System.Int32 IntProp { get; set; }

    [Id(1)]
    public System.Double DoubleProp { get; set; }

    [Id(2)]
    public System.Single FloatProp { get; set; }

    [Id(3)]
    public System.Int64 LongProp { get; set; }

    [Id(4)]
    public System.Boolean BoolProp { get; set; }

    [Id(5)]
    public System.Byte ByteProp { get; set; }

    [Id(6)]
    public System.Int16 ShortProp { get; set; }

    [Id(7)]
    public System.Char CharProp { get; set; }

    [Id(8)]
    public System.UInt32 UIntProp { get; set; }

    [Id(9)]
    public System.UInt64 ULongProp { get; set; }

    [Id(10)]
    public System.UInt16 UShortProp { get; set; }

    [Id(11)]
    public System.SByte SByteProp { get; set; }

    [Id(12)]
    public System.Decimal DecimalProp { get; set; }

    [Id(13)]
    public System.DateTime DateTimeProp { get; set; }

    [Id(14)]
    public System.DateTimeOffset DateTimeOffsetProp { get; set; }

    [Id(15)]
    public System.TimeSpan TimeSpanProp { get; set; }

    [Id(16)]
    public System.Guid GuidProp { get; set; }

    [Id(17)]
    public System.Int32[] IntArrayProp { get; set; }
}");

    /// <summary>
    /// Tests serializer generation for complex object graphs.
    /// Verifies handling of:
    /// - Nested object references
    /// - Collections of custom types
    /// - Cyclic references (important for preventing stack overflow)
    /// </summary>
    [Fact]
    public Task TestClassNestedTypes() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using System.Collections.Generic;

namespace TestProject;

[GenerateSerializer]
public class DemoData
{
    [Id(0)]
    public NestedClass1 Nested1 { get; set; }

    [Id(1)]
    public List<NestedClass1> NestedList { get; set; }

    [Id(2)]
    public CyclicClass Cyclic { get; set; }
}

public class NestedClass1
{
    [Id(0)]
    public string Value { get; set; }

    [Id(1)]
    public NestedClass2 Nested2 { get; set; }
}

public class NestedClass2
{
    [Id(0)]
    public string Value { get; set; }

    [Id(1)]
    public int IntProp { get; set; }
}

public class CyclicClass
{
    [Id(0)]
    public CyclicClass Nested { get; set; }

    [Id(1)]
    public string Value { get; set; }
}");

    /// <summary>
    /// Tests the [Alias] attribute for type name aliases.
    /// Aliases allow types to be renamed without breaking serialization compatibility,
    /// essential for versioning and refactoring scenarios.
    /// </summary>
    [Fact]
    public Task TestAlias() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[Alias(""_custom_type_alias_"")]
public class MyTypeAliasClass
{
    [Id(0)]
    public string Name { get; set; }
}

[GenerateSerializer]
public struct MyTypeAliasStruct
{
    [Id(0)]
    public string Name { get; set; }
}
");

    /// <summary>
    /// Tests the [CompoundTypeAlias] attribute for complex type aliases.
    /// Compound aliases can include multiple type components and versions,
    /// supporting advanced versioning scenarios like type migrations.
    /// </summary>
    [Fact]
    public Task TestCompoundTypeAlias() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[Alias(""_custom_type_alias_"")]
public class MyTypeAliasClass
{
}

[GenerateSerializer]
public class MyCompoundTypeAliasBaseClass
{
    [Id(0)]
    public int BaseValue { get; set; }
}

[GenerateSerializer]
[CompoundTypeAlias(""xx_test_xx"", typeof(MyTypeAliasClass), typeof(int), ""1"")]
public class MyCompoundTypeAliasClass : MyCompoundTypeAliasBaseClass
{
    [Id(0)]
    public string Name { get; set; }

    [Id(1)]
    public int Value { get; set; }
}");

    [Fact]
    public Task TestClassWithParameterizedConstructor() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

public interface IMyService { }
public class MyService : IMyService { }

[GenerateSerializer]
public class MyServiceConsumer
{
    private readonly IMyService _service;
    private readonly int _value;

    // Constructor requiring parameters, which the generator should use for activation
    public MyServiceConsumer(IMyService service, int value)
    {
        _service = service;
        _value = value;
    }

    [Id(0)]
    public string Name { get; set; }
}

// Include a type that uses the above class to ensure it's processed
[GenerateSerializer]
public class RootType
{
    [Id(0)]
    public MyServiceConsumer Consumer { get; set; }
}");

    [Fact]
    public Task TestGenericClassWithConstructorParameters() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class GenericWithCtor<T>
{
    [Id(0)]
    private readonly T _value;
    [Id(1)]
    private readonly int _id;

    public GenericWithCtor(T value, int id)
    {
        _value = value;
        _id = id;
    }

    public T Value => _value;
    public int Id => _id;
}

[GenerateSerializer]
public class UsesGenericWithCtor
{
    [Id(0)]
    public GenericWithCtor<string> StringGen { get; set; }
}", snapshotName: nameof(TestGenericClassWithConstructorParameters));

    [Fact]
    public Task TestClassWithNoPublicConstructors() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class NoPublicCtor
{
    private NoPublicCtor() { }

    [Id(0)]
    public int Value { get; set; }
}");

    [Fact]
    public Task TestClassWithOptionalConstructorParameters() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class OptionalCtorParams
{
    [Id(0)]
    private readonly int _x;
    [Id(1)]
    private readonly string _y;

    public OptionalCtorParams(int x = 42, string y = ""default"")
    {
        _x = x;
        _y = y;
    }

    public int X => _x;
    public string Y => _y;
}");

    [Fact]
    public Task TestClassWithInterfaceConstructorParameter() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

public interface IMyInterface { }

[GenerateSerializer]
public class InterfaceCtorParam
{
    [Id(0)]
    private readonly IMyInterface _iface;

    public InterfaceCtorParam(IMyInterface iface)
    {
        _iface = iface;
    }

    public IMyInterface Iface => _iface;
}");

    [Fact]
    public Task TestClassesWithGeneratedActivatorConstructorAnnotation() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class ClassWithGeneratedActivatorConstructor
{
    [Id(0)]
    public int Value { get; set; }

    [Id(1)]
    public string Name { get; set; } = string.Empty;

    [GeneratedActivatorConstructor]
    public ClassWithGeneratedActivatorConstructor()
    {
    }

    public ClassWithGeneratedActivatorConstructor(int value)
    {
        Value = value;
    }
}");

    [Fact]
    public Task TestClassWithGenerateMethodSerializersAnnotation() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using Orleans.Runtime;
using System.Threading.Tasks;

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IMyGrain : IGrainWithIntegerKey
{
    Task<string> SayHello(string name);
}");

    [Fact]
    public async Task ExplicitMethodAlias_TakesPrecedenceOverAnotherOverloadsGeneratedAlias()
    {
        var compilation = await CreateCompilation(
@"using Orleans;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IBasicGrain : IGrainWithIntegerKey
{
    [Alias(""SayHello"")]
    Task<string> SayHello(string name);

    [Alias(""6B0E24A1"")]
    Task<string> SayHello(string name, CancellationToken cancellationToken);
}",
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var generatedSource = ConcatenateGeneratedSources(result);
        Assert.Contains("\"SayHello\"", generatedSource, StringComparison.Ordinal);
        Assert.Contains("\"6B0E24A1\"", generatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibilityOverload_UsesLegacyInvokerIdentityForOutboundCalls()
    {
        var baseline = await CreateCompilation(
@"using Orleans;
using System.Threading.Tasks;

public interface IBasicGrain : IGrainWithIntegerKey
{
    Task Ping(string value);
}",
            "Baseline");
        var legacyMethod = Assert.Single(baseline.GetTypeByMetadataName("IBasicGrain")!.GetMembers().OfType<IMethodSymbol>());
        var legacyMethodId = GeneratedCodeUtilities.CreateHashedMethodId(legacyMethod);
        var compilation = await CreateCompilation(
$@"using Orleans;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IBasicGrain : IGrainWithIntegerKey
{{
    [Alias(""Ping"")]
    Task Ping(string value);

    [Alias(""{legacyMethodId}"")]
    Task Ping(string value, CancellationToken cancellationToken) => Ping(value);
}}",
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);

        var generatedSource = ConcatenateGeneratedSources(result);
        var generatedRoot = CSharpSyntaxTree.ParseText(
                generatedSource,
                cancellationToken: TestContext.Current.CancellationToken)
            .GetCompilationUnitRoot(TestContext.Current.CancellationToken);
        var proxyMethod = Assert.Single(
            generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(static declaration => declaration.Identifier.ValueText == "Proxy_IBasicGrain")
                .Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Ping" && method.ParameterList.Parameters.Count == 1);
        Assert.Contains(
            "global::System.Threading.CancellationToken.None",
            proxyMethod.Body!.ToString(),
            StringComparison.Ordinal);

        var cancellationMethod = Assert.Single(
            compilation.GetTypeByMetadataName("IBasicGrain")!.GetMembers("Ping").OfType<IMethodSymbol>(),
            static method => method.Parameters.Length == 2);
        var cancellationGeneratedId = GeneratedCodeUtilities.CreateHashedMethodId(cancellationMethod);
        var cancellationInvoker = generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText.StartsWith("Invokable_", StringComparison.Ordinal)
                && declaration.Identifier.ValueText.EndsWith(cancellationGeneratedId, StringComparison.Ordinal));
        var alias = Assert.Single(
            cancellationInvoker.AttributeLists.SelectMany(static list => list.Attributes),
            static attribute => attribute.Name.ToString().EndsWith("CompoundTypeAliasAttribute", StringComparison.Ordinal));
        Assert.Contains(legacyMethodId, alias.ToString(), StringComparison.Ordinal);
        var generatedAliasRegistration = Assert.Single(
            generatedSource.Split(Environment.NewLine),
            line => line.Contains($"Add(\"{cancellationGeneratedId}\", typeof(", StringComparison.Ordinal)
                && line.Contains(cancellationInvoker.Identifier.ValueText, StringComparison.Ordinal));
        Assert.Contains(cancellationGeneratedId, generatedAliasRegistration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericCompatibilityOverload_ForwardsToCancellationOverload()
    {
        var baseline = await CreateCompilation(
@"using Orleans;
using System.Threading.Tasks;

public interface IBasicGrain : IGrainWithIntegerKey
{
    Task<T> RoundTrip<T>(T value);
}",
            "Baseline");
        var legacyMethod = Assert.Single(baseline.GetTypeByMetadataName("IBasicGrain")!.GetMembers().OfType<IMethodSymbol>());
        var legacyMethodId = GeneratedCodeUtilities.CreateHashedMethodId(legacyMethod);
        var compilation = await CreateCompilation(
$@"using Orleans;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IBasicGrain : IGrainWithIntegerKey
{{
    [Alias(""RoundTrip"")]
    Task<T> RoundTrip<T>(T value);

    [Alias(""{legacyMethodId}"")]
    Task<T> RoundTrip<T>(T value, CancellationToken cancellationToken) => RoundTrip(value);
}}",
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);

        var generatedRoot = CSharpSyntaxTree.ParseText(
                ConcatenateGeneratedSources(result),
                cancellationToken: TestContext.Current.CancellationToken)
            .GetCompilationUnitRoot(TestContext.Current.CancellationToken);
        var proxyMethod = Assert.Single(
            generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(static declaration => declaration.Identifier.ValueText == "Proxy_IBasicGrain")
                .Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RoundTrip" && method.ParameterList.Parameters.Count == 1);
        var proxyBody = proxyMethod.Body!.ToString();
        Assert.Contains("RoundTrip<T>", proxyBody, StringComparison.Ordinal);
        Assert.Contains("global::System.Threading.CancellationToken.None", proxyBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericCompatibilityOverload_WithStricterConstraints_DoesNotForward()
    {
        var baseline = await CreateCompilation(
@"using Orleans;
using System.Threading.Tasks;

public interface IBasicGrain : IGrainWithIntegerKey
{
    Task<T> RoundTrip<T>(T value);
}",
            "Baseline");
        var legacyMethod = Assert.Single(baseline.GetTypeByMetadataName("IBasicGrain")!.GetMembers().OfType<IMethodSymbol>());
        var legacyMethodId = GeneratedCodeUtilities.CreateHashedMethodId(legacyMethod);
        var compilation = await CreateCompilation(
$@"using Orleans;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IBasicGrain : IGrainWithIntegerKey
{{
    [Alias(""RoundTrip"")]
    Task<T> RoundTrip<T>(T value);

    [Alias(""{legacyMethodId}"")]
    Task<T> RoundTrip<T>(T value, CancellationToken cancellationToken) where T : struct;
}}",
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);

        var generatedRoot = CSharpSyntaxTree.ParseText(
                ConcatenateGeneratedSources(result),
                cancellationToken: TestContext.Current.CancellationToken)
            .GetCompilationUnitRoot(TestContext.Current.CancellationToken);
        var proxyMethod = Assert.Single(
            generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(static declaration => declaration.Identifier.ValueText == "Proxy_IBasicGrain")
                .Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RoundTrip" && method.ParameterList.Parameters.Count == 1);
        Assert.DoesNotContain(
            "global::System.Threading.CancellationToken.None",
            proxyMethod.Body!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitMethodId_TakesPrecedenceOverAnotherOverloadsGeneratedAlias()
    {
        var candidateMethods = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 128).Select(static index => $"    ValueTask Method{index}();"));
        var baseline = await CreateCompilation(
$@"using Orleans;
using Orleans.Runtime;
using System.Threading.Tasks;

public interface IBasicGrain : IGrainWithIntegerKey
{{
{candidateMethods}
}}",
            "Baseline");
        var candidate = baseline.GetTypeByMetadataName("IBasicGrain")!.GetMembers()
            .OfType<IMethodSymbol>()
            .Select(static method => (Method: method, Id: GeneratedCodeUtilities.CreateHashedMethodId(method)))
            .First(static entry => entry.Id.Any(static character => character is >= 'A' and <= 'F'));
        var explicitMethodId = Convert.ToUInt32(candidate.Id, 16).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var methodName = candidate.Method.Name;
        var compilation = await CreateCompilation(
$@"using Orleans;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IBasicGrain : IGrainWithIntegerKey
{{
    [Alias(""{methodName}"")]
    ValueTask {methodName}();

    [Id(0x{candidate.Id}u)]
    ValueTask {methodName}(CancellationToken cancellationToken);
}}",
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var interfaceSymbol = compilation.GetTypeByMetadataName("IBasicGrain")!;
        var cancellationMethod = Assert.Single(
            interfaceSymbol.GetMembers(methodName).OfType<IMethodSymbol>(),
            static method => method.Parameters.Length == 1);
        var cancellationGeneratedId = GeneratedCodeUtilities.CreateHashedMethodId(cancellationMethod);
        var generatedSource = ConcatenateGeneratedSources(result);
        var generatedRoot = CSharpSyntaxTree.ParseText(
                generatedSource,
                cancellationToken: TestContext.Current.CancellationToken)
            .GetCompilationUnitRoot(TestContext.Current.CancellationToken);
        var cancellationInvoker = generatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText.StartsWith("Invokable_", StringComparison.Ordinal)
                && declaration.Identifier.ValueText.EndsWith(cancellationGeneratedId, StringComparison.Ordinal));
        var outboundAlias = Assert.Single(
            cancellationInvoker.AttributeLists.SelectMany(static list => list.Attributes),
            static attribute => attribute.Name.ToString().EndsWith("CompoundTypeAliasAttribute", StringComparison.Ordinal));
        Assert.Contains(candidate.Id, outboundAlias.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(explicitMethodId, outboundAlias.ToString(), StringComparison.Ordinal);
        var registration = Assert.Single(
            generatedSource.Split(Environment.NewLine),
            line => line.Contains($"Add(\"{candidate.Id}\", typeof(", StringComparison.Ordinal));
        Assert.Contains(cancellationGeneratedId, registration, StringComparison.Ordinal);
        var explicitIdRegistration = Assert.Single(
            generatedSource.Split(Environment.NewLine),
            line => line.Contains($"Add(\"{explicitMethodId}\", typeof(", StringComparison.Ordinal));
        Assert.Contains(cancellationGeneratedId, explicitIdRegistration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericCompatibilityOverload_WithReorderedConstraints_Forwards()
    {
        var baseline = await CreateCompilation(
@"using Orleans;
using System.Threading.Tasks;

public interface IBasicGrain : IGrainWithIntegerKey
{
    Task<T> RoundTrip<T>(T value) where T : IFoo, IBar;
}

public interface IFoo { }
public interface IBar { }",
            "Baseline");
        var legacyMethod = Assert.Single(baseline.GetTypeByMetadataName("IBasicGrain")!.GetMembers().OfType<IMethodSymbol>());
        var legacyMethodId = GeneratedCodeUtilities.CreateHashedMethodId(legacyMethod);
        var compilation = await CreateCompilation(
$@"using Orleans;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IBasicGrain : IGrainWithIntegerKey
{{
    [Alias(""RoundTrip"")]
    Task<T> RoundTrip<T>(T value) where T : IFoo, IBar;

    [Alias(""{legacyMethodId}"")]
    Task<T> RoundTrip<T>(T value, CancellationToken cancellationToken) where T : IBar, IFoo;
}}

public interface IFoo {{ }}
public interface IBar {{ }}",
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var generatedSource = ConcatenateGeneratedSources(result);
        Assert.Contains(
            "global::System.Threading.CancellationToken.None",
            generatedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitExtensionAlias_DoesNotSuppressAnotherDeclaringInterfacesGeneratedAlias()
    {
        var baseline = await CreateCompilation(
@"using Orleans;
using Orleans.Runtime;
using System.Threading.Tasks;

public interface IBaseExtensionA : IGrainExtension
{
    ValueTask FlushBuffers();
}",
            "Baseline");
        var method = Assert.Single(baseline.GetTypeByMetadataName("IBaseExtensionA")!.GetMembers().OfType<IMethodSymbol>());
        var generatedMethodId = GeneratedCodeUtilities.CreateHashedMethodId(method);
        var compilation = await CreateCompilation(
$@"using Orleans;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;

public interface IBaseExtensionA : IGrainExtension
{{
    [Alias(""FlushBuffers"")]
    ValueTask FlushBuffers();
}}

public interface IBaseExtensionB : IGrainExtension
{{
    [Alias(""{generatedMethodId}"")]
    ValueTask FlushBuffers(CancellationToken cancellationToken);
}}

[GenerateMethodSerializers(typeof(GrainReference))]
public interface IDerivedExtension : IBaseExtensionA, IBaseExtensionB
{{
}}",
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var derivedInterface = compilation.GetTypeByMetadataName("IDerivedExtension")!;
        var derivedMethods = derivedInterface.AllInterfaces
            .SelectMany(static interfaceType => interfaceType.GetMembers("FlushBuffers"))
            .OfType<IMethodSymbol>()
            .ToArray();
        var legacyGeneratedId = GeneratedCodeUtilities.CreateHashedMethodId(
            Assert.Single(derivedMethods, static method => method.Parameters.Length == 0));
        var cancellationGeneratedId = GeneratedCodeUtilities.CreateHashedMethodId(
            Assert.Single(derivedMethods, static method => method.Parameters.Length == 1));
        var generatedSource = ConcatenateGeneratedSources(result);
        var registrations = generatedSource.Split(Environment.NewLine)
            .Where(line => line.Contains($"Add(\"{generatedMethodId}\", typeof(", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(
            registrations,
            line => line.Contains(
                $"Invokable_IBaseExtensionA_GrainReference_Ext_{legacyGeneratedId}",
                StringComparison.Ordinal));
        Assert.Contains(
            registrations,
            line => line.Contains(
                $"Invokable_IBaseExtensionB_GrainReference_Ext_{cancellationGeneratedId}",
                StringComparison.Ordinal));
    }

    [Fact]
    public Task TestClassWithGenerateSerializerAnnotation() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public enum MyCustomEnum
{
    None,
    One,
    Two,
    Three
}

[GenerateSerializer(GenerateFieldIds = GenerateFieldIds.PublicProperties), Immutable]
public class ClassWithImplicitFieldIds
{
    public string StringValue { get; set; } = string.Empty;
    public MyCustomEnum EnumValue { get; set; }
}");

    /// <summary>
    /// Tests proxy generation for a basic grain interface.
    /// Verifies that the generator creates:
    /// - Proxy class implementing the grain interface
    /// - Method invokers for RPC calls
    /// - Proper integration with Orleans runtime
    /// </summary>
    [Fact]
    public Task TestBasicGrain() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using System.Threading.Tasks;

namespace TestProject;

public interface IBasicGrain : IGrainWithIntegerKey
{
    Task<string> SayHello(string name);
}

[GenerateSerializer]
public class BasicGrain : Grain, IBasicGrain
{
    public Task<string> SayHello(string name)
    {
        return Task.FromResult($""Hello, {name}!"");
    }
}");

    /// <summary>
    /// Tests proxy generation for grains with different key types.
    /// Orleans supports multiple grain key types:
    /// - Integer keys
    /// - GUID keys
    /// - String keys
    /// - Compound keys (primary key + extension)
    /// Each requires different proxy generation logic.
    /// </summary>
    [Fact]
    public Task TestGrainWithDifferentKeyTypes() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using System;
using System.Threading.Tasks;

namespace TestProject;

public interface IMyGrainWithGuidKey : IGrainWithGuidKey
{
    Task<Guid> GetGuidValue();
}

[GenerateSerializer]
public class GrainWithGuidKey : Grain, IMyGrainWithGuidKey
{
    public Task<Guid> GetGuidValue() => Task.FromResult(this.GetPrimaryKey());
}

public interface IMyGrainWithStringKey : IGrainWithStringKey
{
    Task<string> GetStringKey();
}

[GenerateSerializer]
public class GrainWithStringKey : Grain, IMyGrainWithStringKey
{
    public Task<string> GetStringKey() => Task.FromResult(this.GetPrimaryKeyString());
}

public interface IMyGrainWithGuidCompoundKey : IGrainWithGuidCompoundKey
{
    Task<Tuple<Guid, string>> GetGuidAndStringKey();
}

[GenerateSerializer]
public class GrainWithGuidCompoundKey : Grain, IMyGrainWithGuidCompoundKey
{
    public Task<Tuple<Guid, string>> GetGuidAndStringKey()
    {
        Guid primaryKey = this.GetPrimaryKey(out var keyExtension);
        return Task.FromResult(Tuple.Create(primaryKey, keyExtension));
    }
}

public interface IMyGrainWithIntegerCompoundKey : IGrainWithIntegerCompoundKey
{
    Task<Tuple<long, string>> GetIntegerAndStringKey();
}

[GenerateSerializer]
public class GrainWithIntegerCompoundKey : Grain, IMyGrainWithIntegerCompoundKey
{
    public Task<Tuple<long, string>> GetIntegerAndStringKey()
    {
        long primaryKey = this.GetPrimaryKeyLong(out var keyExtension);
        return Task.FromResult(Tuple.Create(primaryKey, keyExtension));
    }
}");

    /// <summary>
    /// Tests grain proxy generation with complex method signatures.
    /// Verifies that the generator correctly handles:
    /// - Multiple parameters
    /// - Custom types as parameters and return values
    /// - Async Task return types
    /// </summary>
    [Fact]
    public Task TestGrainComplexGrain() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using System.Threading;
using System.Threading.Tasks;

namespace TestProject;

[GenerateSerializer]
public class ComplexData
{
    [Id(0)]
    public int IntValue { get; set; }

    [Id(1)]
    public string StringValue { get; set; }
}

public interface IComplexGrain : IGrainWithIntegerKey
{
    Task<ComplexData> ProcessData(int inputInt, string inputString, ComplexData data, CancellationToken ctx);
}

[GenerateSerializer]
public class ComplexGrain : Grain, IComplexGrain
{
    public Task<ComplexData> ProcessData(int inputInt, string inputString, ComplexData data, CancellationToken ctx)
    {
        var result = new ComplexData
        {
            IntValue = inputInt * 2 + data.IntValue,
            StringValue = $""Processed: {inputString}"" + data.StringValue
        };
        return Task.FromResult(result);
    }
}");

    [Fact]
    public Task TestGrainWithMultipleInterfaces() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using System.Threading.Tasks;

namespace TestProject;

public interface IGrainA : IGrainWithIntegerKey
{
    Task<string> MethodA(string input);
}

public interface IGrainB : IGrainWithIntegerKey
{
    Task<string> MethodB(string input);
}

public class RealGrain : Grain, IGrainA, IGrainB
{
    public Task<string> MethodA(string input)
    {
        return Task.FromResult($""GrainA: {input}!"");
    }

    public Task<string> MethodB(string input)
    {
        return Task.FromResult($""GrainB: {input}!"");
    }
}");

    [Fact]
    public Task TestGrainMethodAnnotatedWithResponseTimeout() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using System.Threading.Tasks;

namespace TestProject;

public interface IResponseTimeoutGrain : IGrainWithIntegerKey
{
    [ResponseTimeout(""00:00:10"")]
    Task<string> LongRunningMethod(string input);
}

public class ResponseTimeoutGrain : Grain, IResponseTimeoutGrain
{
    public Task<string> LongRunningMethod(string input)
    {
        // Simulate a long-running operation
        return Task.FromResult($""ResponseTimeoutGrain: {input}!"");
    }
}");

    [Fact]
    public Task TestGrainMethodAnnotatedWithInvokableBaseType() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using Orleans.Runtime;

using System;
using System.Threading.Tasks;

namespace TestProject;

[InvokableCustomInitializer(nameof(LoggerRequest.SetLoggingOptions))]
[InvokableBaseType(typeof(GrainReference), typeof(Task), typeof(LoggerRequest))]
[AttributeUsage(AttributeTargets.Method)]
public sealed class LoggingRcpAttribute : Attribute
{
    public LoggingRcpAttribute(string options)
    {
    }
}

public abstract class LoggerRequest : RequestBase
{
    public void SetLoggingOptions(string options)
    {
    }
}

public interface IHelloGrain : IGrainWithIntegerKey
{
    [LoggingRcp(""Hello"")]
    Task<string> SayHello(string greeting);
}

[GenerateSerializer]
public class HelloGrain : Grain, IHelloGrain
{
    public Task<string> SayHello(string greeting)
    {
        return Task.FromResult($""Hello, {greeting}!"");
    }
}");

    [Fact]
    public Task TestWithUseActivatorAnnotation() => AssertSuccessfulSourceGeneration(
@"using Orleans;
using Orleans.Serialization.Activators;

namespace TestProject;

[UseActivator]
public class DemoClass
{
}

[RegisterActivator]
internal sealed class DemoClassActivator : IActivator<DemoClass>
{
    public DemoClass Create() => new DemoClass();
}");

    [Fact]
    public Task TestWithSerializerTransparentAnnotation() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[SerializerTransparent]
public abstract class DemoTransparentClass
{
}");

    [Fact]
    public Task TestWithSuppressReferenceTrackingAttribute() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer, SuppressReferenceTracking]
public class DemoClass
{
    [Id(0)]
    public string Value { get; set; } = string.Empty;
}");

    [Fact]
    public Task TestWithOmitDefaultMemberValuesAnnotation() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer, OmitDefaultMemberValues]
public class DemoClass
{
    [Id(0)]
    public string Value { get; set; }
}");

    /// <summary>
    /// Tests that invokable deduplication works correctly when multiple grain interfaces
    /// share a common base interface. The base method <c>DoWork</c> is inherited by both
    /// <c>IGrainA</c> and <c>IGrainB</c>, but the generator should produce only one
    /// invokable class for that method per declaring interface, not duplicate it for each
    /// derived interface.
    /// This behavior is currently handled by the active proxy pipeline via the
    /// <c>_invokableMethodDescriptions</c> dictionary on the proxy-generation state.
    /// </summary>
    [Fact]
    public async Task SharedBaseInterfaceMethodProducesDeduplicatedInvokable()
    {
        var code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IBaseGrain : IGrainWithGuidKey
            {
                Task DoWork();
            }

            public interface IGrainA : IBaseGrain
            {
                Task DoExtraA();
            }

            public interface IGrainB : IBaseGrain
            {
                Task DoExtraB();
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));

        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);

        var generatedSource = ConcatenateGeneratedSources(result);

        // The base method DoWork is declared on IBaseGrain. Regardless of how many interfaces
        // inherit from IBaseGrain, only one invokable should be generated for DoWork on IBaseGrain.
        var invokableDoWorkCount = System.Text.RegularExpressions.Regex.Matches(
            generatedSource, @"sealed class Invokable_IBaseGrain_GrainReference_\w+").Count;
        Assert.Equal(1, invokableDoWorkCount);

        // Each derived interface should get its own invokable for its unique method.
        Assert.Contains("Invokable_IGrainA_GrainReference_", generatedSource);
        Assert.Contains("Invokable_IGrainB_GrainReference_", generatedSource);
    }

    [Fact]
    public async Task DerivedInterfaceInheritingGenerateMethodSerializersProducesProxy()
    {
        var code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IBaseGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }

            public interface IDerivedGrain : IBaseGrain
            {
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));

        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);

        var generatedSource = ConcatenateGeneratedSources(result);
        Assert.Contains("Proxy_IDerivedGrain", generatedSource);
        Assert.Contains("Invokable_IBaseGrain_GrainReference_", generatedSource);
    }

    /// <summary>
    /// Tests that the compilation-level reference-assembly extraction path correctly handles
    /// types discovered via [GenerateCodeForDeclaringAssembly]. These types cannot be found by
    /// ForAttributeWithMetadataName since they live in other assemblies.
    /// </summary>
    [Fact]
    public async Task GeneratesSerializersForReferencedAssemblyTypesViaGenerateCodeForDeclaringAssembly()
    {
        var libraryCode = """
            using Orleans;

            namespace LibraryProject;

            [GenerateSerializer]
            public class LibraryDto
            {
                [Id(0)]
                public string Name { get; set; } = string.Empty;

                [Id(1)]
                public int Value { get; set; }
            }
        """;

        var libraryCompilation = await CreateCompilation(libraryCode, "LibraryProject");
        Assert.Empty(
            libraryCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        // Run the generator on the library so its output assembly includes the generated metadata
        var libraryGeneratorResult = RunSourceGenerator(libraryCompilation);
        Assert.Empty(libraryGeneratorResult.Diagnostics);

        // Build the consumer that references the library via [GenerateCodeForDeclaringAssembly]
        var consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.LibraryDto))]
        """;

        var consumerCompilation = await CreateCompilation(consumerCode, "ConsumerProject");

        // Add the library as a metadata reference so the consumer can see its types
        consumerCompilation = consumerCompilation.AddReferences(libraryCompilation.ToMetadataReference());
        Assert.Empty(
            consumerCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        var consumerResult = RunSourceGenerator(consumerCompilation);
        Assert.Empty(consumerResult.Diagnostics);
        Assert.NotEmpty(consumerResult.GeneratedSources);

        var generatedSource = ConcatenateGeneratedSources(consumerResult);

        // The generator should produce a serializer (codec) for LibraryDto from the referenced assembly
        Assert.Contains("LibraryDto", generatedSource);
        Assert.Contains("Codec", generatedSource);
    }

    [Fact]
    public async Task GeneratesSerializersForReferencedNestedAndGenericAssemblyTypesViaGenerateCodeForDeclaringAssembly()
    {
        var libraryCode = """
            using Orleans;

            namespace LibraryProject;

            [GenerateSerializer]
            public sealed class GenericDto<T>
            {
                [Id(0)]
                public T Value { get; set; } = default!;
            }

            public sealed class Container
            {
                [GenerateSerializer]
                public sealed class NestedDto
                {
                    [Id(0)]
                    public int Value { get; set; }
                }

                [GenerateSerializer]
                public sealed class NestedGenericDto<T>
                {
                    [Id(0)]
                    public T Value { get; set; } = default!;
                }
            }
        """;

        var libraryCompilation = await CreateCompilation(libraryCode, "LibraryProject");
        Assert.Empty(
            libraryCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        var consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.Container.NestedGenericDto<>))]
        """;

        var consumerCompilation = (await CreateCompilation(consumerCode, "ConsumerProject"))
            .AddReferences(libraryCompilation.ToMetadataReference());
        Assert.Empty(
            consumerCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        var consumerResult = RunSourceGenerator(consumerCompilation);
        Assert.Empty(consumerResult.Diagnostics);

        Assert.Contains(
            consumerResult.GeneratedSources,
            source => source.HintName.Contains("LibraryProject.GenericDto", StringComparison.Ordinal));
        Assert.Contains(
            consumerResult.GeneratedSources,
            source => source.HintName.Contains("LibraryProject.Container.NestedDto", StringComparison.Ordinal));
        Assert.Contains(
            consumerResult.GeneratedSources,
            source => source.HintName.Contains("LibraryProject.Container.NestedGenericDto", StringComparison.Ordinal));

        var generatedSource = ConcatenateGeneratedSources(consumerResult);
        Assert.Contains("global::LibraryProject.GenericDto", generatedSource);
        Assert.Contains("global::LibraryProject.Container.NestedDto", generatedSource);
        Assert.Contains("global::LibraryProject.Container.NestedGenericDto", generatedSource);
    }

    [Fact]
    public async Task ReferencedSerializerResolutionUsesAssemblyMetadataIdentityWhenConsumerShadowsFullName()
    {
        var libraryCode = """
            using Orleans;

            namespace LibraryProject
            {
                public sealed class Marker
                {
                }
            }

            namespace Shadowed
            {
                [GenerateSerializer]
                public sealed class DuplicateDto
                {
                    [Id(0)]
                    public int LibraryValue { get; set; }
                }
            }
        """;

        var libraryCompilation = await CreateCompilation(libraryCode, "LibraryProject");
        Assert.Empty(
            libraryCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        var consumerCode = """
            using Orleans;

            [assembly: GenerateCodeForDeclaringAssembly(typeof(LibraryProject.Marker))]

            namespace Shadowed;

            public sealed class DuplicateDto
            {
                public string ConsumerValue { get; set; } = string.Empty;
            }
        """;

        var consumerCompilation = (await CreateCompilation(consumerCode, "ConsumerProject"))
            .AddReferences(libraryCompilation.ToMetadataReference());
        Assert.Empty(
            consumerCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        var consumerResult = RunSourceGenerator(consumerCompilation);
        Assert.Empty(consumerResult.Diagnostics);

        var generatedSource = ConcatenateGeneratedSources(consumerResult);
        Assert.Contains("LibraryValue", generatedSource);
        Assert.DoesNotContain("ConsumerValue", generatedSource);
    }

    [Fact]
    public async Task GeneratesProxiesForGenericAndNestedInterfaces()
    {
        var code = """
            using Orleans;
            using Orleans.Runtime;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateMethodSerializers(typeof(GrainReference))]
            public interface IGenericGrain<T> : IGrainWithIntegerKey
            {
                Task<T> Echo(T value);
            }

            public sealed class Container
            {
                [GenerateMethodSerializers(typeof(GrainReference))]
                public interface INestedGrain : IGrainWithIntegerKey
                {
                    Task Ping();
                }

                [GenerateMethodSerializers(typeof(GrainReference))]
                public interface INestedGenericGrain<T> : IGrainWithIntegerKey
                {
                    Task<T> Echo(T value);
                }
            }
        """;

        var compilation = await CreateCompilation(code, "TestProject");
        Assert.Empty(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);

        Assert.Contains(
            result.GeneratedSources,
            source => source.HintName.Contains("IGenericGrain", StringComparison.Ordinal));
        Assert.Contains(
            result.GeneratedSources,
            source => source.HintName.Contains("Container.INestedGrain", StringComparison.Ordinal));
        Assert.Contains(
            result.GeneratedSources,
            source => source.HintName.Contains("Container.INestedGenericGrain", StringComparison.Ordinal));

        var generatedSource = ConcatenateGeneratedSources(result);
        Assert.Contains("Proxy_IGenericGrain", generatedSource);
        Assert.Contains("Proxy_INestedGrain", generatedSource);
        Assert.Contains("Proxy_INestedGenericGrain", generatedSource);
    }

    /// <summary>
    /// Tests that the generator emits a warning when [GenerateSerializer] is used in a reference assembly.
    /// Reference assemblies contain only metadata, no implementation, so generating serializers
    /// in them is incorrect. This test ensures developers get proper diagnostics for this mistake.
    /// </summary>
    [Fact]
    public async Task EmitsWarningForGenerateSerializerInReferenceAssembly()
    {
        var code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class RefAsmType
            {
                [Id(0)]
                public string Value { get; set; } = string.Empty;
            }
        """;

        // The ReferenceAssemblyAttribute marks the assembly as a reference assembly.
        // This triggers the Orleans code generator's logic to emit a diagnostic if [GenerateSerializer] is used in such an assembly.
        var compilation = await CreateCompilation(code, "TestProject");
        var referenceAssemblyAttribute = SyntaxFactory.Attribute(SyntaxFactory.ParseName("System.Runtime.CompilerServices.ReferenceAssemblyAttribute"));
        var assemblyAttr = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(referenceAssemblyAttribute))
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)));
        var root = (CSharpSyntaxNode)compilation.SyntaxTrees[0].GetRoot(TestContext.Current.CancellationToken);
        var newRoot = ((CompilationUnitSyntax)root).AddAttributeLists(assemblyAttr);
        var newTree = compilation.SyntaxTrees[0].WithRootAndOptions(newRoot, compilation.SyntaxTrees[0].Options);

        // leave only syntaxTree with the ReferenceAssemblyAttribute
        compilation = compilation.RemoveSyntaxTrees(compilation.SyntaxTrees[0]).AddSyntaxTrees(newTree);

        var result = RunSourceGenerator(compilation);
        Assert.Contains(result.Diagnostics, d => d.Id == DiagnosticRuleId.ReferenceAssemblyWithGenerateSerializer);
    }

    [Fact]
    public async Task EmitsInvalidFieldIdDiagnosticForImplicitPublicProperties()
    {
        const string code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class AutoDto
            {
                public string Value { get; set; } = string.Empty;
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        Assert.Empty(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error));

        var result = RunSourceGenerator(compilation);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == CanNotGenerateImplicitFieldIdsDiagnostic.DiagnosticId);

        Assert.Equal(DiagnosticRuleId.CanNotGenerateImplicitFieldIds, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("AutoDto", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedCustomAttributeBuildPropertiesAreIgnored()
    {
        var code = """
            using System;
            using Orleans;

            namespace TestProject;

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
            public sealed class CustomGenerateSerializerAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property)]
            public sealed class CustomImmutableAttribute : Attribute
            {
            }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
            public sealed class CustomAliasAttribute : Attribute
            {
                public CustomAliasAttribute(string value)
                {
                }
            }

            [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
            public sealed class CustomIdAttribute : Attribute
            {
                public CustomIdAttribute(uint id)
                {
                }
            }

            [CustomGenerateSerializer, CustomAlias("custom")]
            public class DemoData
            {
                [CustomId(0), CustomImmutable]
                public string Value { get; set; } = string.Empty;
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var baselineResult = RunSourceGenerator(compilation);
        var configuredResult = RunSourceGenerator(
            compilation,
            new Dictionary<string, string>
            {
                ["build_property.orleans_immutableattributes"] = "TestProject.CustomImmutableAttribute",
                ["build_property.orleans_idattributes"] = "TestProject.CustomIdAttribute",
                ["build_property.orleans_aliasattributes"] = "TestProject.CustomAliasAttribute",
                ["build_property.orleans_generateserializerattributes"] = "TestProject.CustomGenerateSerializerAttribute",
            });

        Assert.NotEmpty(baselineResult.GeneratedSources);
        Assert.NotEmpty(configuredResult.GeneratedSources);
        Assert.Equal(
            baselineResult.GeneratedSources.Select(source => source.HintName).OrderBy(name => name),
            configuredResult.GeneratedSources.Select(source => source.HintName).OrderBy(name => name));
        Assert.Equal(
            ConcatenateGeneratedSources(baselineResult),
            ConcatenateGeneratedSources(configuredResult));
        Assert.Equal(
            baselineResult.Diagnostics.Select(d => d.Id).OrderBy(id => id),
            configuredResult.Diagnostics.Select(d => d.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task GlobalGenerateFieldIdsOption_AllowsImplicitPublicProperties()
    {
        var code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class DemoData
            {
                public string Value { get; set; } = string.Empty;
                public int Count { get; set; }
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var baselineResult = RunSourceGenerator(compilation);
        var configuredResult = RunSourceGenerator(
            compilation,
            new Dictionary<string, string>
            {
                ["build_property.orleans_generatefieldids"] = "PublicProperties",
            });

        Assert.Contains(baselineResult.Diagnostics, d => d.Id == DiagnosticRuleId.CanNotGenerateImplicitFieldIds);
        Assert.Empty(configuredResult.Diagnostics);
        Assert.Contains(configuredResult.GeneratedSources, source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompatibilityInvokersOption_GeneratesAdditionalInheritedInvokables()
    {
        var code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            public interface IBaseGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }

            public interface IDerivedGrain : IBaseGrain
            {
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var baselineResult = RunSourceGenerator(compilation);
        var configuredResult = RunSourceGenerator(
            compilation,
            new Dictionary<string, string>
            {
                ["build_property.orleansgeneratecompatibilityinvokers"] = "true",
            });

        Assert.Empty(baselineResult.Diagnostics);
        Assert.Empty(configuredResult.Diagnostics);

        var baselineSource = ConcatenateGeneratedSources(baselineResult);
        var configuredSource = ConcatenateGeneratedSources(configuredResult);

        Assert.True(
            CountGeneratedInvokableClasses(configuredSource) > CountGeneratedInvokableClasses(baselineSource),
            "Enabling compatibility invokers should generate additional invokable classes for inherited grain methods.");

        var compatibilityInvokers = configuredResult.GeneratedSources
            .Where(static source => source.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal))
            .SelectMany(static source => CSharpSyntaxTree.ParseText(source.SourceText.ToString().TrimStart('\uFEFF'))
                .GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            .Where(static declaration => declaration.Identifier.ValueText.StartsWith("Invokable_IDerivedGrain_", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(compatibilityInvokers);

        var metadataSource = configuredResult.GeneratedSources
            .Single(source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();
        Assert.Contains(
            "typeof(OrleansCodeGen.TestProject.Invokable_IDerivedGrain_",
            metadataSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibilityInvokersOption_ExplicitAliasSuppressesInheritedGeneratedAlias()
    {
        var baseline = await CreateCompilation(
            """
            using Orleans;
            using System.Threading.Tasks;

            public interface IBaseGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }
            """,
            "Baseline");
        var baseMethod = Assert.Single(baseline.GetTypeByMetadataName("IBaseGrain")!.GetMembers().OfType<IMethodSymbol>());
        var legacyMethodId = GeneratedCodeUtilities.CreateHashedMethodId(baseMethod);
        var compilation = await CreateCompilation(
            $$"""
            using Orleans;
            using System.Threading;
            using System.Threading.Tasks;

            public interface IBaseGrain : IGrainWithIntegerKey
            {
                Task Ping();
            }

            public interface IDerivedGrain : IBaseGrain
            {
                [Alias("{{legacyMethodId}}")]
                Task Ping(CancellationToken cancellationToken);
            }
            """,
            "TestProject");

        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
        var result = RunSourceGenerator(
            compilation,
            new Dictionary<string, string>
            {
                ["build_property.orleansgeneratecompatibilityinvokers"] = "true",
            });
        Assert.Empty(result.Diagnostics);

        var derivedInterface = compilation.GetTypeByMetadataName("IDerivedGrain")!;
        var cancellationMethod = Assert.Single(
            derivedInterface.GetMembers("Ping").OfType<IMethodSymbol>(),
            static method => method.Parameters.Length == 1);
        var cancellationGeneratedId = GeneratedCodeUtilities.CreateHashedMethodId(cancellationMethod);
        var generatedSource = ConcatenateGeneratedSources(result);
        var registration = Assert.Single(
            generatedSource.Split(Environment.NewLine),
            line => line.Contains($"Add(\"{legacyMethodId}\", typeof(", StringComparison.Ordinal)
                && line.Contains("Invokable_IDerivedGrain_", StringComparison.Ordinal));
        Assert.Contains(cancellationGeneratedId, registration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibilityInvokersOption_PreservesCrossNamespaceInvokables()
    {
        var code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace A
            {
                public interface IFoo : IGrainWithIntegerKey
                {
                    Task Ping();
                }
            }

            namespace B
            {
                public interface IFoo : A.IFoo
                {
                }
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var result = RunSourceGenerator(
            compilation,
            new Dictionary<string, string>
            {
                ["build_property.orleansgeneratecompatibilityinvokers"] = "true",
            });

        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            result.GeneratedSources,
            static source => source.SourceText.ToString().Contains(
                "namespace OrleansCodeGen.B",
                StringComparison.Ordinal)
                && source.SourceText.ToString().Contains(
                    "class Invokable_IFoo_",
                    StringComparison.Ordinal));

        var outputCompilation = compilation.AddSyntaxTrees(
            result.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName)));
        Assert.Empty(
            outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task ProviderMetadata_EmitsCompilableModuleInitializer()
    {
        var code = """
            using Orleans;

            [assembly: RegisterProvider("Test", "Clustering", "Client", typeof(TestProject.Provider))]

            namespace TestProject;

            public sealed class Provider
            {
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var result = RunSourceGenerator(compilation);
        var metadataSource = result.GeneratedSources
            .Single(source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal))
            .SourceText
            .ToString();

        Assert.Contains("global::System.Runtime.CompilerServices.ModuleInitializerAttribute", metadataSource, StringComparison.Ordinal);
        Assert.Contains("global::Orleans.Serialization.Configuration.ProviderMetadataRegistry.Register", metadataSource, StringComparison.Ordinal);

        var outputCompilation = compilation
            .AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(
            result.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName)));
        Assert.Empty(
            outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task AttachDebuggerFalseOption_DoesNotChangeOutput()
    {
        var code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public class DemoData
            {
                [Id(0)]
                public string Value { get; set; } = string.Empty;
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var baselineResult = RunSourceGenerator(compilation);
        var configuredResult = RunSourceGenerator(
            compilation,
            new Dictionary<string, string>
            {
                ["build_property.orleans_attachdebugger"] = "false",
            });

        Assert.Empty(baselineResult.Diagnostics);
        Assert.Empty(configuredResult.Diagnostics);
        Assert.Equal(
            baselineResult.GeneratedSources.Select(source => source.HintName).OrderBy(name => name),
            configuredResult.GeneratedSources.Select(source => source.HintName).OrderBy(name => name));
        Assert.Equal(
            ConcatenateGeneratedSources(baselineResult),
            ConcatenateGeneratedSources(configuredResult));
    }

    [Fact]
    public async Task GeneratedSources_EmitMetadataLast()
    {
        var code = """
            using Orleans;
            using System.Threading.Tasks;

            namespace TestProject;

            [GenerateSerializer]
            public class DemoData
            {
                [Id(0)]
                public string Value { get; set; } = string.Empty;
            }

            public interface IMyGrain : IGrainWithIntegerKey
            {
                Task<DemoData> Get();
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var result = RunSourceGenerator(compilation);

        Assert.Empty(result.Diagnostics);

        var emittedHintNames = result.GeneratedSources
            .Where(static source => !string.IsNullOrWhiteSpace(source.SourceText.ToString()))
            .Select(static source => source.HintName)
            .ToArray();

        Assert.NotEmpty(emittedHintNames);
        Assert.Contains(emittedHintNames, static hintName => hintName.Contains(".orleans.ser.", StringComparison.Ordinal));
        Assert.Contains(emittedHintNames, static hintName => hintName.Contains(".orleans.proxy.", StringComparison.Ordinal));
        Assert.EndsWith(".orleans.metadata.g.cs", emittedHintNames[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedInvokableActivators_AreRegisteredInMetadata()
    {
        var code = """
            using System;
            using System.Threading.Tasks;
            using Orleans;
            using Orleans.Runtime;

            namespace TestProject;

            [InvokableBaseType(typeof(GrainReference), typeof(Task<>), typeof(ActivatingTaskRequest<>))]
            [AttributeUsage(AttributeTargets.Method)]
            public sealed class ActivatingAttribute : Attribute
            {
            }

            public abstract class ActivatingTaskRequest<TResult> : TaskRequest<TResult>
            {
                [GeneratedActivatorConstructor]
                protected ActivatingTaskRequest(IServiceProvider serviceProvider)
                {
                }
            }

            public interface IActivatingGrain : IGrainWithIntegerKey
            {
                [Activating]
                Task<string> GetValue();
            }

            public interface INormalGrain : IGrainWithIntegerKey
            {
                Task<string> GetValue();
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var result = RunSourceGenerator(compilation);

        Assert.Empty(result.Diagnostics);

        var emittedActivatorNames = GetGeneratedClassNames(result, ".orleans.proxy.", "Activator_Invokable_");
        var registeredActivatorNames = GetRegisteredGeneratedInvokableActivatorNames(result);

        Assert.Single(emittedActivatorNames);
        Assert.Equal(emittedActivatorNames, registeredActivatorNames);
        Assert.Contains(emittedActivatorNames, static name => name.Contains("IActivatingGrain", StringComparison.Ordinal));
        Assert.DoesNotContain(registeredActivatorNames, static name => name.Contains("INormalGrain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordPrimaryConstructorFieldIds_AreNotDuplicatedOrDropped()
    {
        var code = """
            using Orleans;

            namespace TestProject;

            [GenerateSerializer]
            public sealed record PrimaryCtorRecord(
                [property: Id(0)] string Value,
                [field: Id(1)] int Count)
            {
                [Id(2)]
                public string Extra { get; init; } = string.Empty;
            }
            """;

        var compilation = await CreateCompilation(code, "TestProject");
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(),
            driverOptions: new GeneratorDriverOptions(default));
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics,
            TestContext.Current.CancellationToken);
        var result = driver.GetRunResult().Results.Single();

        Assert.Empty(generatorDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(
            outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var serializerSource = Assert.Single(
            result.GeneratedSources,
            static source => source.HintName.Contains(".orleans.ser.", StringComparison.Ordinal)
                && source.SourceText.ToString().Contains("Codec_PrimaryCtorRecord", StringComparison.Ordinal));
        var serializerText = serializerSource.SourceText.ToString();

        Assert.Equal(1, CountOccurrences(serializerText, "instance.Value"));
        Assert.Equal(1, CountOccurrences(serializerText, "instance.Count"));
        Assert.Equal(1, CountOccurrences(serializerText, "instance.Extra"));
        Assert.Equal(1, CountOccurrences(serializerText, "if (id == 0U)"));
        Assert.Equal(1, CountOccurrences(serializerText, "if (id == 1U)"));
        Assert.Equal(1, CountOccurrences(serializerText, "if (id == 2U)"));
    }

    [Fact]
    public async Task RpcResponseFactoriesGenerateConcreteClosedGraph()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IResponses : IGrainWithIntegerKey
            {
                Task<bool> Boolean();
                ValueTask<int> Integer();
                Task<Payload> Reference();
                Task<int> Repeated();
                Task<System.Collections.Generic.KeyValuePair<string, string>> Pair();
            }
            [GenerateSerializer, Alias("rpc.payload")]
            public sealed class Payload
            {
                [Id(0)] public int Value { get; set; }
                [Id(1)] public Payload Next { get; set; }
                [Id(2)] public Orleans.Serialization.Invocation.Response<Payload> Envelope { get; set; }
            }
            """);
        var result = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.GeneratedSources, static source => source.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("new global::Orleans.Serialization.Invocation.PooledResponseCodec<bool, global::Orleans.Serialization.Codecs.BoolCodec>", source);
        Assert.Contains("new global::Orleans.Serialization.Invocation.PooledResponseCopier<int, global::Orleans.Serialization.Cloning.ShallowCopier<int>>", source);
        Assert.Contains("PooledResponseCodec<global::TestProject.Payload, global::OrleansCodeGen.TestProject.Codec_Payload>", source);
        Assert.Contains("new global::OrleansCodeGen.TestProject.Codec_Payload(provider)", source);
        Assert.Contains("new global::Orleans.Serialization.Codecs.KeyValuePairCodec<string, string>", source);
        Assert.Contains("new global::Orleans.Serialization.Codecs.KeyValuePairCopier<string, string>", source);
        Assert.Contains("caller => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.TestProject.Codec_Payload>(caller, provider)", source);
        Assert.Contains("caller => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.TestProject.Copier_Payload>(caller, provider)", source);
        Assert.Equal(1, CountOccurrences(source, "options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<int>,"));
        Assert.DoesNotContain("RuntimeFeature.IsDynamicCodeSupported", source);
        Assert.Contains("codecDependencies: new global::System.Type[]", source);
        Assert.Contains("copierDependencies: new global::System.Type[]", source);
        Assert.DoesNotContain("GetService<global::Orleans.Serialization.Codecs.IFieldCodec<", source);
        Assert.DoesNotContain("GetService<global::Orleans.Serialization.Cloning.IDeepCopier<", source);
        Assert.DoesNotContain("RequireExplicitTypeRegistration", source);
        Assert.DoesNotContain("MakeGenericType", source);
        Assert.DoesNotContain("WellKnownTypeAliases", source);
        Assert.Contains("WellKnownTypeAliases.Add(\"rpc.payload\"", ConcatenateGeneratedSources(result));
        Assert.Contains("options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response>", source);
        Assert.Contains("AbstractTypeSerializer<global::Orleans.Serialization.Invocation.Response>", source);
        Assert.Contains("global::Orleans.Serialization.Codecs.ObjectCopier.DeepCopy(input, context)", source);
        Assert.Contains("new global::OrleansCodeGen.Orleans.Serialization.Invocation.Codec_CompletedResponse(", source);
        Assert.Contains("Create() => global::Orleans.Serialization.Invocation.CompletedResponse.Instance", source);
        var outputCompilation = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(result.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText,
                options: new CSharpParseOptions().WithPreprocessorSymbols("NET5_0_OR_GREATER"), path: source.HintName)));
        Assert.Empty(outputCompilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData("Task<T> Get<T>();")]
    [InlineData("Task<System.Collections.Generic.List<T>> Get<T>();")]
    [InlineData("Task<object> Get();")]
    public async Task RpcResponseFactoriesDiagnoseUnresolvedNativeResults(string method)
    {
        var compilation = await CreateCompilation($$"""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IResponses : IGrainWithIntegerKey { {{method}} }
            """);
        var result = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ORLEANS0116", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("serializer context", diagnostic.GetMessage());
        Assert.NotEqual(Location.None, diagnostic.Location);
    }

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary<string, int>")]
    [InlineData("System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, int>>")]
    public async Task RpcResponseFactoriesRequireExplicitDictionaryComparerContract(string resultType)
    {
        var compilation = await CreateCompilation($$"""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IResponses : IGrainWithIntegerKey { Task<{{resultType}}> Get(); }
            """);
        var native = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        var diagnostic = Assert.Single(native.Diagnostics);
        Assert.Equal("ORLEANS0116", diagnostic.Id);
        Assert.Contains("comparer contract", diagnostic.GetMessage());
        Assert.Contains("Dictionary", diagnostic.GetMessage());
        Assert.DoesNotContain(native.GeneratedSources, static source => source.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal));
        var jit = RunSourceGenerator(compilation);
        Assert.Empty(jit.Diagnostics);
        Assert.Contains(jit.GeneratedSources, static source => source.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RpcResponseFactoriesRootCanonicalModelsInCommonPipeline()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IResponses : IGrainWithIntegerKey { Task<Payload> Get(); }
            [GenerateSerializer]
            public sealed class Payload
            {
                [Id(0)] public IReadOnlyList<System.Tuple<int, string>> Members { get; private set; }
                public Payload(IReadOnlyList<System.Tuple<int, string>> members) => Members = members;
            }
            """);
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.GeneratedSources, static source => source.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        Assert.DoesNotContain("RequireExplicitTypeRegistration", source);
        Assert.DoesNotContain("RuntimeFeature.IsDynamicCodeSupported", source);
        Assert.Contains("new global::OrleansCodeGen.TestProject.Codec_Payload(", source);
        Assert.Contains("new global::OrleansCodeGen.TestProject.Copier_Payload(", source);
        Assert.Contains("PooledResponseCodec<global::TestProject.Payload, global::OrleansCodeGen.TestProject.Codec_Payload>", source);
        Assert.Contains("AddDefaultSerializerService<global::Orleans.Serialization.Activators.IActivator<global::TestProject.Payload>,", source);
        Assert.Contains("OrleansGeneratedCodeHelper.CreateDefaultReferenceTypeActivator<global::TestProject.Payload>()", source);
        Assert.DoesNotContain("MakeGenericType", source);
        var output = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(result.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText,
                options: new CSharpParseOptions().WithPreprocessorSymbols("NET5_0_OR_GREATER"), path: item.HintName)));
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var strict = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        Assert.Contains(strict.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcReferencedGeneratedActivatorFactoryIsClosed(bool referenceAssembly)
    {
        var producer = await CreateCompilation("""
            using Orleans;
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ClosureConsumer")]
            namespace ReferencedClosure;
            [GenerateSerializer, Immutable]
            internal sealed class Payload
            {
                [Id(0)] private int _value;
                [System.NonSerialized] internal readonly object _state = new();
                [GeneratedActivatorConstructor]
                public Payload(int value) => _value = value;
            }
            """, "ClosureProducer");
        var generated = RunSourceGenerator(producer);
        Assert.Empty(generated.Diagnostics);
        producer = producer.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName)));
        using var image = new System.IO.MemoryStream();
        var emitted = producer.Emit(image,
            options: new Microsoft.CodeAnalysis.Emit.EmitOptions(metadataOnly: referenceAssembly, includePrivateMembers: !referenceAssembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var compilation = (await CreateCompilation("""
            using Orleans;
            namespace ClosureProof;
            [GenerateSerializer, Immutable]
            internal struct Package
            {
                [Id(0)] public ReferencedClosure.Payload Context { get; set; }
                [Id(1)] public System.Collections.Generic.IList<System.Tuple<ReferencedClosure.Payload[], int>> Nested { get; set; }
            }
            """, "ClosureConsumer")).AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        var type = compilation.GetTypeByMetadataName("ReferencedClosure.Payload");
        Assert.NotNull(type);
        var services = new GeneratorServices(compilation, new CodeGeneratorOptions());
        var graph = SerializerFactoryGenerator.CreateRpcModelRoot(services, type, TestContext.Current.CancellationToken);
        Assert.NotNull(graph);
        Assert.Contains("Activator_Payload", graph.ConfigurationStatements);
        Assert.Contains("Codec_Payload", graph.ConfigurationStatements);
        var construction = SerializerFactoryGenerator.CreateRpcConstructionRoot(services, type, TestContext.Current.CancellationToken);
        Assert.Contains("Activator_Payload", construction.ConfigurationStatements);
        Assert.Contains("Codec_Payload", construction.ConfigurationStatements);
        var package = compilation.GetTypeByMetadataName("ClosureProof.Package");
        Assert.NotNull(package);
        Assert.True(compilation.IsSymbolAccessibleWithin(package, compilation.Assembly));
        Assert.NotNull(SerializerFactoryGenerator.CreateRpcModelRoot(services, package, TestContext.Current.CancellationToken));
        var parent = SerializerFactoryGenerator.CreateRpcConstructionRoot(services,
            compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(package), TestContext.Current.CancellationToken);
        Assert.True(parent.ConfigurationStatements.Contains("Activator_Payload", StringComparison.Ordinal), parent.ConfigurationStatements);
        Assert.Contains("ArrayCodec<global::ReferencedClosure.Payload>", parent.ConfigurationStatements);
        Assert.Contains("TupleCodec<global::ReferencedClosure.Payload[], int>", parent.ConfigurationStatements);
        Assert.Contains("IFieldCodec<global::System.Collections.Generic.IList<", parent.ConfigurationStatements);
        Assert.Contains("dependencies: new global::System.Type[] { typeof(global::Orleans.Serialization.Codecs.IFieldCodec<global::System.Tuple<", parent.ConfigurationStatements);
    }

    [Fact]
    public async Task RpcImmutableConstructionFactoriesShareCanonicalSurrogateServices()
    {
        var compilation = await CreateCompilation("""
            namespace ClosureProof;
            public class Root
            {
                public System.Collections.Immutable.ImmutableArray<int> Array;
                public System.Collections.Immutable.ImmutableList<int> List;
                public System.Collections.Immutable.ImmutableQueue<int> Queue;
                public System.Collections.Immutable.ImmutableStack<int> Stack;
                public System.Collections.Immutable.ImmutableHashSet<int> Set;
                public System.Collections.Immutable.ImmutableSortedSet<int> SortedSet;
                public System.Collections.Immutable.ImmutableDictionary<string, int> Dictionary;
                public System.Collections.Immutable.ImmutableSortedDictionary<string, int> SortedDictionary;
            }
            """, $"ImmutableClosureProof{Guid.NewGuid():N}");
        var services = new GeneratorServices(compilation, new CodeGeneratorOptions());
        var statements = new System.Text.StringBuilder();
        var assertions = new System.Text.StringBuilder();
        foreach (var field in compilation.GetTypeByMetadataName("ClosureProof.Root")!.GetMembers().OfType<IFieldSymbol>())
        {
            var type = (INamedTypeSymbol)field.Type;
            var graph = SerializerFactoryGenerator.CreateRpcConstructionRoot(services, type, TestContext.Current.CancellationToken);
            var codec = services.LibraryTypes.WellKnownCodecs.FindByUnderlyingType(type.OriginalDefinition)!.CodecType.Construct([.. type.TypeArguments]);
            var surrogate = ((INamedTypeSymbol)codec.InstanceConstructors.Single().Parameters.Single().Type).TypeArguments.Single();
            var typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var codecName = codec.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var surrogateName = surrogate.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            Assert.Contains($"IValueSerializer<{surrogateName}>", graph.ConfigurationStatements);
            Assert.Contains($"new {codecName}(", graph.ConfigurationStatements);
            statements.AppendLine(graph.ConfigurationStatements);
            assertions.AppendLine($"""
                if (!ReferenceEquals(provider.GetCodec<{typeName}>(),
                    Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<{codecName}>(null, provider))) return false;
                if (!ReferenceEquals(provider.GetValueSerializer<{surrogateName}>(), provider.GetCodec<{surrogateName}>())) return false;
                _ = provider.GetDeepCopier<{typeName}>();
                """);
        }
        var exercise = $$"""
            using System;
            using System.Collections.Immutable;
            using Microsoft.Extensions.DependencyInjection;
            using Orleans.Serialization;
            using Orleans.Serialization.Serializers;
            public static class ImmutableClosureProof
            {
                public static bool Run()
                {
                    using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
                    {
                        {{statements}}
                    })).BuildServiceProvider();
                    var provider = services.GetRequiredService<CodecProvider>();
                    {{assertions}}
                    var serializer = services.GetRequiredService<Serializer>();
                    var source = ImmutableDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase).Add("Key", 47);
                    var result = serializer.Deserialize<ImmutableDictionary<string, int>>(serializer.SerializeToArray(source));
                    var sorted = ImmutableSortedDictionary.Create<string, int>(StringComparer.OrdinalIgnoreCase).Add("Key", 59);
                    var sortedResult = serializer.Deserialize<ImmutableSortedDictionary<string, int>>(serializer.SerializeToArray(sorted));
                    return result["KEY"] == 47 && sortedResult["KEY"] == 59
                        && ReferenceEquals(result.KeyComparer, source.KeyComparer)
                        && ReferenceEquals(sortedResult.KeyComparer, sorted.KeyComparer);
                }
            }
            """;
        compilation = compilation.AddReferences(
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions).Assembly.Location))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(exercise, cancellationToken: TestContext.Current.CancellationToken));
        using var image = new System.IO.MemoryStream();
        var emitted = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        Assert.Equal(true, assembly.GetType("ImmutableClosureProof")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public async Task RpcResponseFactoriesConstructPartialModelRootsWithinPendingGraphs()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            namespace TestProject;
            [GenerateSerializer]
            public sealed class Payload
            {
                [Id(0)] public int Value { get; private set; }
                [Id(1)] public System.Collections.Generic.IReadOnlyList<System.Tuple<Item, string>> Members { get; private set; }
                public Payload(int value) { Value = value; Members = new[] { System.Tuple.Create(new Item { Value = value }, "entry") }; }
            }
            [GenerateSerializer]
            public sealed class Item { [Id(0)] public int Value { get; set; } }
            """, $"RootActivatorProof{Guid.NewGuid():N}");
        var payload = compilation.GetTypeByMetadataName("TestProject.Payload");
        Assert.NotNull(payload);
        var graph = SerializerFactoryGenerator.CreateRpcModelRoot(new GeneratorServices(compilation, new CodeGeneratorOptions()),
            payload, TestContext.Current.CancellationToken);
        Assert.NotNull(graph);
        Assert.Contains("provider.GetCodec<global::System.Collections.Generic.IReadOnlyList<", graph.ConfigurationStatements);
        Assert.DoesNotContain("options.AddDefaultSerializer<global::System.Collections.Generic.IReadOnlyList<", graph.ConfigurationStatements);
        var generated = RunSourceGenerator(compilation);
        Assert.Empty(generated.Diagnostics);
        var statements = graph.ConfigurationStatements.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var activationRegistration = string.Join(Environment.NewLine, statements.Where(static statement => statement.Contains("CreateDefaultReferenceTypeActivator", StringComparison.Ordinal)));
        var modelRegistrations = string.Join(Environment.NewLine, statements.Where(static statement => !statement.Contains("CreateDefaultReferenceTypeActivator", StringComparison.Ordinal)));
        var exerciseSource = $$"""
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using Orleans.Serialization;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Invocation;
            using Orleans.Serialization.Serializers;
            public sealed class RootContext : TypeManifestProviderBase
            {
                private readonly bool includeActivator;
                private int attempts;
                public RootContext(bool includeActivator) { this.includeActivator = includeActivator; }
                protected override void ConfigureInner(TypeManifestOptions options)
                {
                    if (includeActivator)
                    {
                        {{activationRegistration}}
                    }
                    {{modelRegistrations}}
                    options.AddSerializerService<ConstructionProbe>(provider =>
                    {
                        var probe = new ConstructionProbe(
                            provider.GetCodec<Response<TestProject.Payload>>(),
                            provider.GetDeepCopier<Response<TestProject.Payload>>(),
                            provider.GetActivator<TestProject.Payload>());
                        if (++attempts == 1)
                        {
                            ConstructionProbe.Failed = probe;
                            throw new InvalidOperationException("injected root failure");
                        }
                        return probe;
                    });
                }
            }
            public sealed class ConstructionProbe
            {
                public static ConstructionProbe Failed;
                public Orleans.Serialization.Codecs.IFieldCodec<Response<TestProject.Payload>> Codec { get; }
                public Orleans.Serialization.Cloning.IDeepCopier<Response<TestProject.Payload>> Copier { get; }
                public Orleans.Serialization.Activators.IActivator<TestProject.Payload> Activator { get; }
                public ConstructionProbe(
                    Orleans.Serialization.Codecs.IFieldCodec<Response<TestProject.Payload>> codec,
                    Orleans.Serialization.Cloning.IDeepCopier<Response<TestProject.Payload>> copier,
                    Orleans.Serialization.Activators.IActivator<TestProject.Payload> activator)
                { Codec = codec; Copier = copier; Activator = activator; }
            }
            public static class RootProof
            {
                public static bool Run(bool includeActivator)
                {
                    using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(new RootContext(includeActivator))).BuildServiceProvider();
                    var provider = services.GetRequiredService<CodecProvider>();
                    var committed = provider.GetCodec<int>();
                    try
                    {
                        Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ConstructionProbe>(null, provider);
                        throw new InvalidOperationException("expected injected root failure");
                    }
                    catch (InvalidOperationException error) when (error.Message == "injected root failure") { }
                    var rebuilt = Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ConstructionProbe>(null, provider);
                    using var original = Response.FromResult(new TestProject.Payload(47));
                    using var copied = services.GetRequiredService<DeepCopier>().Copy(original);
                    var value = copied.GetResult<TestProject.Payload>();
                    var codec = provider.GetCodec<Response<TestProject.Payload>>();
                    using var sessions = services.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>().GetSession();
                    var output = new System.Buffers.ArrayBufferWriter<byte>();
                    var writer = Orleans.Serialization.Buffers.Writer.Create(output, sessions);
                    codec.WriteField(ref writer, 0, typeof(Response<TestProject.Payload>), (Response<TestProject.Payload>)original);
                    writer.Commit();
                    var activator = provider.GetActivator<TestProject.Payload>();
                    return value.Value == 47 && !ReferenceEquals(original.Result, value)
                        && value.Members[0].Item1.Value == 47 && value.Members[0].Item2 == "entry"
                        && !ReferenceEquals(((TestProject.Payload)original.Result).Members, value.Members)
                        && !ReferenceEquals(((TestProject.Payload)original.Result).Members[0].Item1, value.Members[0].Item1)
                        && ReferenceEquals(activator, provider.GetActivator<TestProject.Payload>())
                        && ReferenceEquals(rebuilt.Activator, activator) && ReferenceEquals(rebuilt.Codec, codec)
                        && !ReferenceEquals(ConstructionProbe.Failed.Activator, activator)
                        && !ReferenceEquals(ConstructionProbe.Failed.Copier, rebuilt.Copier)
                        && ReferenceEquals(committed, provider.GetCodec<int>())
                        && activator.Create().Value == 0 && output.WrittenCount > 0;
                }
            }
            """;
        var output = compilation.AddReferences(
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName)))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(exerciseSource, cancellationToken: TestContext.Current.CancellationToken));
        using var image = new System.IO.MemoryStream();
        var emit = output.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        var run = assembly.GetType("RootProof")!.GetMethod("Run")!;
        var rejected = Assert.Throws<System.Reflection.TargetInvocationException>(() => run.Invoke(null, [false]));
        var error = Assert.IsType<InvalidOperationException>(rejected.InnerException);
        Assert.Contains("Dependency injection cannot resolve", error.Message);
        Assert.Contains("graph is unpublished", error.Message);
        Assert.Equal(true, run.Invoke(null, [true]));
    }

    [Theory]
    [InlineData("[UseActivator]", "public Payload(int value) => Value = value;")]
    [InlineData("", "[GeneratedActivatorConstructor] public Payload(int value) => Value = value;")]
    public async Task RpcResponseFactoriesPreserveCustomActivatorSelection(string attribute, string constructor)
    {
        var compilation = await CreateCompilation($$"""
            using Orleans;
            namespace TestProject;
            [GenerateSerializer]
            {{attribute}}
            public sealed class Payload
            {
                [Id(0)] public int Value { get; private set; }
                {{constructor}}
            }
            """);
        var payload = compilation.GetTypeByMetadataName("TestProject.Payload");
        Assert.NotNull(payload);
        var graph = SerializerFactoryGenerator.CreateRpcModelRoot(new GeneratorServices(compilation, new CodeGeneratorOptions()),
            payload, TestContext.Current.CancellationToken);
        if (attribute.Contains("UseActivator", StringComparison.Ordinal))
        {
            Assert.Null(graph);
            return;
        }

        Assert.NotNull(graph);
        Assert.DoesNotContain("CreateDefaultReferenceTypeActivator", graph.ConfigurationStatements);
        Assert.DoesNotContain("CreateDefaultValueTypeActivator", graph.ConfigurationStatements);
        if (constructor.Contains("GeneratedActivatorConstructor", StringComparison.Ordinal))
        {
            Assert.Contains("new global::OrleansCodeGen.TestProject.Activator_Payload(", graph.ConfigurationStatements);
        }
    }

    [Fact]
    public async Task RpcResponseFactoriesRejectInaccessibleReferencedGeneratedActivators()
    {
        var library = await CreateCompilation("""
            using Orleans;
            namespace ReferencedActivation;
            [GenerateSerializer]
            public sealed class Payload
            {
                [Id(0)] public int Value { get; private set; }
                [GeneratedActivatorConstructor]
                public Payload(int value) => Value = value;
            }
            """, "ReferencedActivation");
        var generated = RunSourceGenerator(library);
        Assert.Empty(generated.Diagnostics);
        library = library.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName)));
        using var image = new System.IO.MemoryStream();
        var emitted = library.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var consumer = (await CreateCompilation("""
            using Orleans;
            using System.Threading.Tasks;
            public interface IContract : IGrainWithIntegerKey
            {
                Task<ReferencedActivation.Payload> Get();
            }
            """, "ActivationConsumer")).AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        var type = consumer.GetTypeByMetadataName("ReferencedActivation.Payload");
        Assert.NotNull(type);
        Assert.Null(SerializerFactoryGenerator.CreateRpcModelRoot(new GeneratorServices(consumer, new CodeGeneratorOptions()),
            type, TestContext.Current.CancellationToken));
        var strict = RunSourceGenerator(consumer, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        Assert.Contains(strict.Diagnostics, static diagnostic => diagnostic.Id == "ORLEANS0116");
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("ValueTask")]
    public async Task RpcResponseFactoriesGenerateCompletionOnlyContracts(string returnType)
    {
        var compilation = await CreateCompilation($$"""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface ICompletion : IGrainWithIntegerKey { {{returnType}} Done(); }
            """);
        var result = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.GeneratedSources, static item => item.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response>", source);
        Assert.Contains("options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.CompletedResponse>", source);
        Assert.Contains("Codec_CompletedResponse", source);
        Assert.DoesNotContain("PooledResponseCodec<", source);
        var output = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(result.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText,
                options: new CSharpParseOptions().WithPreprocessorSymbols("NET5_0_OR_GREATER"), path: item.HintName)));
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task RpcResponseFactoriesLeaveOneWayContractsWithoutResponses()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            namespace TestProject;
            public interface IOneWay : IGrainWithIntegerKey { void Send(); }
            """);
        var result = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.GeneratedSources, static item => item.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RpcResponseFactoriesUseReferencedHotReloadConstructorContracts()
    {
        var library = await CreateCompilation("""
            using Orleans;
            namespace ReferencedResults;
            [GenerateSerializer]
            public sealed class Payload { [Id(0)] public int Value { get; set; } }
            """, "ReferencedResults");
        var generated = RunSourceGenerator(library, new Dictionary<string, string> { ["build_property.orleanshotreload"] = "true" });
        Assert.Empty(generated.Diagnostics);
        library = library.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText, path: item.HintName)));
        using var image = new System.IO.MemoryStream();
        var emit = library.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

        var consumer = (await CreateCompilation("namespace Consumer { }", "Consumer"))
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray()))
            .AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location));
        var payload = consumer.GetTypeByMetadataName("ReferencedResults.Payload");
        Assert.NotNull(payload);
        var services = new GeneratorServices(consumer, new CodeGeneratorOptions { HotReloadSafe = false });
        Assert.True(SerializerFactoryGenerator.TryCreate(services, [payload], TestContext.Current.CancellationToken, out var graph, out var failure), failure?.Reason);
        Assert.NotNull(graph);
        Assert.Contains("new global::OrleansCodeGen.ReferencedResults.Codec_Payload(provider)", graph.ConfigurationStatements);

        var contextSource = $$"""
            public sealed class ConsumerContext : Orleans.Serialization.SerializerContext
            {
                protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options)
                {
                    {{graph.ConfigurationStatements}}
                }
            }
            """;
        var output = consumer.AddSyntaxTrees(CSharpSyntaxTree.ParseText(contextSource, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcResponseFactoriesIgnoreStaticInterfaceHelpers(bool hasInstanceMethod)
    {
        var instanceMethod = hasInstanceMethod ? "Task<int> Invoke();" : "";
        var compilation = await CreateCompilation($$"""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IHelpers : IGrainWithIntegerKey
            {
                static Task<string> SupportedHelper() => Task.FromResult("local");
                static Task<object> UnsupportedHelper() => Task.FromResult(new object());
                static Task<T> GenericHelper<T>(T value) => Task.FromResult(value);
                static Task CompletionHelper() => Task.CompletedTask;
            }
            public interface IContract : IHelpers
            {
                static ValueTask<bool> LocalHelper() => ValueTask.FromResult(true);
                {{instanceMethod}}
            }
            """);
        var result = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        Assert.Empty(result.Diagnostics);
        var factories = result.GeneratedSources.Where(static item => item.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal)).ToArray();
        if (!hasInstanceMethod)
        {
            Assert.Empty(factories);
            return;
        }

        var source = Assert.Single(factories).SourceText.ToString();
        Assert.Contains("Response<int>", source);
        Assert.DoesNotContain("Response<string>", source);
        Assert.DoesNotContain("Response<bool>", source);
        Assert.DoesNotContain("Response<object>", source);
        var output = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(result.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText,
                options: new CSharpParseOptions().WithPreprocessorSymbols("NET5_0_OR_GREATER"), path: item.HintName)));
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task RpcResponseFactoriesPreserveAllAliasesAndMetadataOnlyComponents()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            [CompoundTypeAlias("rpc.marker")]
            public sealed class Marker { }
            [GenerateSerializer, CompoundTypeAlias("rpc.multiple", "2"), CompoundTypeAlias("rpc.multiple", "1")]
            public sealed class Payload { [Id(0)] public int Value { get; set; } }
            [GenerateSerializer, CompoundTypeAlias(typeof(Marker), "payload")]
            public sealed class NestedPayload { [Id(0)] public int Value { get; set; } }
            public interface IAliases : IGrainWithIntegerKey
            {
                Task<Payload> Multiple();
                Task<NestedPayload> Nested();
            }
            """);
        var generated = RunSourceGenerator(compilation, new Dictionary<string, string> { ["build_property.publishaot"] = "true" });
        Assert.Empty(generated.Diagnostics);
        var metadata = Assert.Single(generated.GeneratedSources, static item => item.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("GetOrAdd(\"rpc.multiple\")", metadata);
        Assert.Contains(".Add(\"1\", typeof(global::TestProject.Payload))", metadata);
        Assert.Contains(".Add(\"2\", typeof(global::TestProject.Payload))", metadata);

        var services = new GeneratorServices(compilation, new CodeGeneratorOptions());
        var multiple = compilation.GetTypeByMetadataName("TestProject.Payload");
        var nested = compilation.GetTypeByMetadataName("TestProject.NestedPayload");
        Assert.NotNull(multiple);
        Assert.NotNull(nested);
        Assert.True(SerializerFactoryGenerator.TryCreate(services, [multiple, nested], TestContext.Current.CancellationToken, out var graph, out var failure), failure?.Reason);
        Assert.NotNull(graph);
        Assert.Contains("GetOrAdd(\"rpc.multiple\").Add(\"2\", typeof(global::TestProject.Payload))", graph.ConfigurationStatements);
        Assert.Contains("GetOrAdd(\"rpc.multiple\").Add(\"1\", typeof(global::TestProject.Payload))", graph.ConfigurationStatements);
        Assert.Contains("options.CompoundTypeAliases.Add(\"rpc.marker\", typeof(global::TestProject.Marker))", graph.ConfigurationStatements);
        Assert.Contains("options.CompoundTypeAliases.GetOrAdd(typeof(global::TestProject.Marker)).Add(\"payload\"", graph.ConfigurationStatements);
        Assert.DoesNotContain(graph.Registrations.Keys, static type => type.Name == "Marker");
        Assert.Equal(1, CountOccurrences(graph.ConfigurationStatements, "options.CompoundTypeAliases.Add(\"rpc.marker\""));

        var contextSource = $$"""
            public sealed class AliasContext : Orleans.Serialization.SerializerContext
            {
                protected override void ConfigureInner(Orleans.Serialization.Configuration.TypeManifestOptions options)
                {
                    {{graph.ConfigurationStatements}}
                }
            }
            """;
        var output = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText,
                options: new CSharpParseOptions().WithPreprocessorSymbols("NET5_0_OR_GREATER"), path: item.HintName)))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(contextSource, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task RpcResponseFactoriesVisitRecursiveAliasMetadataOnce()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            namespace TestProject;
            [CompoundTypeAlias(typeof(SecondMarker), "first")]
            public sealed class FirstMarker { }
            [CompoundTypeAlias(typeof(FirstMarker), "second")]
            public sealed class SecondMarker { }
            [GenerateSerializer, CompoundTypeAlias(typeof(FirstMarker), "payload")]
            public sealed class Payload { [Id(0)] public int Value { get; set; } }
            """);
        var payload = compilation.GetTypeByMetadataName("TestProject.Payload");
        Assert.NotNull(payload);
        Assert.True(SerializerFactoryGenerator.TryCreate(new GeneratorServices(compilation, new CodeGeneratorOptions()),
            [payload], TestContext.Current.CancellationToken, out var graph, out var failure), failure?.Reason);
        Assert.NotNull(graph);
        Assert.Equal(1, CountOccurrences(graph.ConfigurationStatements, ".Add(\"first\", typeof(global::TestProject.FirstMarker))"));
        Assert.Equal(1, CountOccurrences(graph.ConfigurationStatements, ".Add(\"second\", typeof(global::TestProject.SecondMarker))"));
        Assert.DoesNotContain(graph.Registrations.Keys, static type => type.Name.EndsWith("Marker", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TestProject.GeneratedValue<int>", false)]
    [InlineData("TestProject.Box<byte>", true)]
    [InlineData("TestProject.Box<int>", true)]
    public async Task RpcResponseFactoriesCloseCanonicalFullGraphServices(string rootName, bool arrayCase)
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            namespace TestProject;
            [GenerateSerializer]
            public struct GeneratedValue<T> { [Id(0)] public T Value { get; set; } }
            [GenerateSerializer]
            public sealed class Box<T> { [Id(0)] public T[] Value { get; set; } }
            """, $"FullGraphProof{Guid.NewGuid():N}");
        var definition = compilation.GetTypeByMetadataName(arrayCase ? "TestProject.Box`1" : "TestProject.GeneratedValue`1");
        Assert.NotNull(definition);
        var element = compilation.GetSpecialType(rootName.Contains("byte", StringComparison.Ordinal) ? SpecialType.System_Byte : SpecialType.System_Int32);
        var type = definition.Construct(element);
        Assert.True(SerializerFactoryGenerator.TryCreate(new GeneratorServices(compilation, new CodeGeneratorOptions()),
            [type], TestContext.Current.CancellationToken, out var graph, out var failure), failure?.Reason);
        Assert.NotNull(graph);
        var generated = RunSourceGenerator(compilation);
        Assert.Empty(generated.Diagnostics);
        var exercise = arrayCase ? $$"""
            var provider = services.GetRequiredService<CodecProvider>();
            var serializer = new Serializer<{{rootName}}>(provider.GetCodec<{{rootName}}>(), sessions);
            var copier = new DeepCopier<{{rootName}}>(provider.GetDeepCopier<{{rootName}}>(), services.GetRequiredService<Orleans.Serialization.Cloning.CopyContextPool>());
            var original = new {{rootName}} { Value = new[] { ({{(rootName.Contains("byte", StringComparison.Ordinal) ? "byte" : "int")}})7, ({{(rootName.Contains("byte", StringComparison.Ordinal) ? "byte" : "int")}})9 } };
            var copy = copier.Copy(original);
            var wire = serializer.SerializeToArray(original);
            using var legacy = new ServiceCollection().AddSerializer(builder => builder.AddAssembly(typeof({{rootName}}).Assembly)).BuildServiceProvider();
            var legacySerializer = new Serializer<{{rootName}}>(
                legacy.GetRequiredService<CodecProvider>().GetCodec<{{rootName}}>(),
                legacy.GetRequiredService<SerializerSessionPool>());
            var legacyWire = legacySerializer.SerializeToArray(original);
            var result = serializer.Deserialize(wire);
            copy.Value[0] = 3;
            return original.Value[0] == 7 && result.Value[0] == 7 && result.Value[1] == 9
                && !ReferenceEquals(original, copy) && !ReferenceEquals(original.Value, copy.Value)
                && System.MemoryExtensions.SequenceEqual<byte>(wire, legacyWire)
                && (!typeof({{rootName}}).GenericTypeArguments[0].Equals(typeof(byte))
                    || provider.GetCodec<byte[]>() is Orleans.Serialization.Codecs.ByteArrayCodec
                        && provider.GetDeepCopier<byte[]>() is Orleans.Serialization.Codecs.ByteArrayCopier);
            """ : """
            var provider = services.GetRequiredService<CodecProvider>();
            var serializer = new ValueSerializer<TestProject.GeneratedValue<int>>(provider, sessions);
            var original = new TestProject.GeneratedValue<int> { Value = 47 };
            var output = new System.Buffers.ArrayBufferWriter<byte>();
            serializer.Serialize(ref original, output);
            var result = new TestProject.GeneratedValue<int>();
            serializer.Deserialize(output.WrittenMemory, ref result);
            return result.Value == 47 && ReferenceEquals(
                provider.GetValueSerializer<TestProject.GeneratedValue<int>>(),
                provider.GetCodec<TestProject.GeneratedValue<int>>());
            """;
        var contextSource = $$"""
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using Orleans.Serialization;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Serializers;
            using Orleans.Serialization.Session;
            public sealed class ClosedContext : SerializerContext
            {
                protected override void ConfigureInner(TypeManifestOptions options)
                {
                    {{graph.ConfigurationStatements}}
                }
            }
            public static class FullGraphProof
            {
                public static bool Run()
                {
                    using var services = new ServiceCollection().AddSerializerContext(new ClosedContext()).BuildServiceProvider();
                    var sessions = services.GetRequiredService<SerializerSessionPool>();
                    {{exercise}}
                }
            }
            """;
        var outputCompilation = compilation.AddReferences(
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText, path: item.HintName)))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(contextSource, cancellationToken: TestContext.Current.CancellationToken));
        using var image = new System.IO.MemoryStream();
        var emit = outputCompilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        Assert.Equal(true, assembly.GetType("FullGraphProof")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public async Task RpcResponseFactoriesPreserveGenericArrayGraphCycles()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            namespace TestProject;
            [GenerateSerializer]
            public sealed class Box<T> { [Id(0)] public T[] Value { get; set; } }
            [GenerateSerializer]
            public sealed class Node { [Id(0)] public Box<Node> Children { get; set; } }
            """, $"ArrayCycleProof{Guid.NewGuid():N}");
        var node = compilation.GetTypeByMetadataName("TestProject.Node");
        Assert.NotNull(node);
        Assert.True(SerializerFactoryGenerator.TryCreate(new GeneratorServices(compilation, new CodeGeneratorOptions()),
            [node], TestContext.Current.CancellationToken, out var graph, out var failure), failure?.Reason);
        Assert.NotNull(graph);
        var generated = RunSourceGenerator(compilation);
        Assert.Empty(generated.Diagnostics);
        var exercise = $$"""
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using Orleans.Serialization;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Serializers;
            public sealed class CycleContext : SerializerContext
            {
                protected override void ConfigureInner(TypeManifestOptions options)
                {
                    {{graph.ConfigurationStatements}}
                }
            }
            public static class ArrayCycleProof
            {
                public static bool Run()
                {
                    using var services = new ServiceCollection().AddSerializerContext(new CycleContext()).BuildServiceProvider();
                    var provider = services.GetRequiredService<CodecProvider>();
                    var serializer = new Serializer<TestProject.Node>(provider.GetCodec<TestProject.Node>(),
                        services.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>());
                    var copier = new DeepCopier<TestProject.Node>(provider.GetDeepCopier<TestProject.Node>(),
                        services.GetRequiredService<Orleans.Serialization.Cloning.CopyContextPool>());
                    var original = new TestProject.Node();
                    original.Children = new TestProject.Box<TestProject.Node> { Value = new[] { original, original } };
                    var copy = copier.Copy(original);
                    var result = serializer.Deserialize(serializer.SerializeToArray(original));
                    return !ReferenceEquals(original, copy) && ReferenceEquals(copy, copy.Children.Value[0])
                        && ReferenceEquals(copy.Children.Value[0], copy.Children.Value[1])
                        && ReferenceEquals(result, result.Children.Value[0])
                        && ReferenceEquals(result.Children.Value[0], result.Children.Value[1]);
                }
            }
            """;
        var output = compilation.AddReferences(
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName)))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(exercise, cancellationToken: TestContext.Current.CancellationToken));
        using var image = new System.IO.MemoryStream();
        var emit = output.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        Assert.Equal(true, assembly.GetType("ArrayCycleProof")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RpcResponseFactoriesCloseCanonicalTupleConstruction(int arity)
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            namespace TestProject;
            [GenerateSerializer]
            public sealed class Item { [Id(0)] public int Value { get; set; } }
            """, $"TupleConstructionProof{Guid.NewGuid():N}");
        var item = compilation.GetTypeByMetadataName("TestProject.Item");
        Assert.NotNull(item);
        var elementTypes = Enumerable.Repeat<Microsoft.CodeAnalysis.ITypeSymbol>(item, arity).ToArray();
        var tuple = compilation.GetTypeByMetadataName($"System.Tuple`{arity}")!.Construct(elementTypes);
        Assert.True(SerializerFactoryGenerator.TryCreate(new GeneratorServices(compilation, new CodeGeneratorOptions()),
            [tuple], TestContext.Current.CancellationToken, out var graph, out var failure), failure?.Reason);
        Assert.NotNull(graph);
        var generated = RunSourceGenerator(compilation);
        Assert.Empty(generated.Diagnostics);
        var tupleName = tuple.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var arguments = string.Join(", ", Enumerable.Repeat("item", arity));
        var values = string.Join(" && ", Enumerable.Range(1, arity).Select(index => $"copy.Item{index}.Value == 47 && result.Item{index}.Value == 47"));
        var aliasing = arity > 1 ? "&& ReferenceEquals(copy.Item1, copy.Item2) && ReferenceEquals(result.Item1, result.Item2)" : "";
        var exercise = $$"""
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using Orleans.Serialization;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Serializers;
            public sealed class TupleContext : SerializerContext
            {
                protected override void ConfigureInner(TypeManifestOptions options)
                {
                    {{graph.ConfigurationStatements}}
                }
            }
            public static class TupleProof
            {
                public static bool Run()
                {
                    using var services = new ServiceCollection().AddSerializerContext(new TupleContext()).BuildServiceProvider();
                    var provider = services.GetRequiredService<CodecProvider>();
                    var serializer = new Serializer<{{tupleName}}>(provider.GetCodec<{{tupleName}}>(),
                        services.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>());
                    var copier = new DeepCopier<{{tupleName}}>(provider.GetDeepCopier<{{tupleName}}>(),
                        services.GetRequiredService<Orleans.Serialization.Cloning.CopyContextPool>());
                    var item = new TestProject.Item { Value = 47 };
                    var original = new {{tupleName}}({{arguments}});
                    var copy = copier.Copy(original);
                    var wire = serializer.SerializeToArray(original);
                    var result = serializer.Deserialize(wire);
                    using var legacy = new ServiceCollection().AddSerializer(builder => builder.AddAssembly(typeof(TestProject.Item).Assembly)).BuildServiceProvider();
                    var oldSerializer = new Serializer<{{tupleName}}>(legacy.GetRequiredService<CodecProvider>().GetCodec<{{tupleName}}>(),
                        legacy.GetRequiredService<Orleans.Serialization.Session.SerializerSessionPool>());
                    return !ReferenceEquals(original, copy) && !ReferenceEquals(item, copy.Item1)
                        && {{values}} {{aliasing}}
                        && System.MemoryExtensions.SequenceEqual<byte>(wire, oldSerializer.SerializeToArray(original));
                }
            }
            """;
        var output = compilation.AddReferences(
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText, path: source.HintName)))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(exercise, cancellationToken: TestContext.Current.CancellationToken));
        using var image = new System.IO.MemoryStream();
        var emit = output.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        Assert.Equal(true, assembly.GetType("TupleProof")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("Task<int>", true)]
    [InlineData("Task", true)]
    [InlineData("void", false)]
    public async Task RpcResponseFactoriesConstructActualTupleArgumentProxies(string returnType, bool responseExpected)
    {
        var compilation = await CreateCompilation($$"""
            using System;
            using System.Threading.Tasks;
            using Orleans;
            using Orleans.Runtime;
            using Orleans.Serialization.Cloning;
            using Orleans.Serialization.Invocation;
            using Orleans.Serialization.Serializers;
            namespace TestProject;
            [DefaultInvokableBaseType(typeof(Task<>), typeof(TaskRequest<>))]
            [DefaultInvokableBaseType(typeof(Task), typeof(TaskRequest))]
            [DefaultInvokableBaseType(typeof(void), typeof(VoidRequest))]
            public abstract class TupleProxyBase
            {
                protected TupleProxyBase(ICodecProvider provider, CopyContextPool pool)
                {
                    CodecProvider = provider;
                    CopyContextPool = pool;
                }
                protected ICodecProvider CodecProvider { get; }
                protected CopyContextPool CopyContextPool { get; }
                protected T GetInvokable<T>() where T : class, IInvokable, new() => new T();
                protected ValueTask<T> InvokeAsync<T>(IInvokable body) => default;
                protected ValueTask InvokeAsync(IInvokable body) => default;
                protected void Invoke(IInvokable body) { }
            }
            [GenerateMethodSerializers(typeof(TupleProxyBase))]
            public interface IContract
            {
                {{returnType}} InvokeTuple(System.Collections.Generic.List<Tuple<SiloAddress, DateTime>> value);
            }
            """, $"ProxyConstructionProof{Guid.NewGuid():N}");
        var generated = RunSourceGenerator(compilation);
        Assert.Empty(generated.Diagnostics);
        var source = Assert.Single(generated.GeneratedSources, static item => item.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("new global::Orleans.Serialization.Codecs.TupleCopier<global::Orleans.Runtime.SiloAddress, global::System.DateTime>", source);
        Assert.Equal(responseExpected, source.Contains("options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response>", StringComparison.Ordinal));
        var tuple = compilation.GetTypeByMetadataName("System.Tuple`2")!.Construct(
            compilation.GetTypeByMetadataName("Orleans.Runtime.SiloAddress")!, compilation.GetTypeByMetadataName("System.DateTime")!);
        var list = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(tuple);
        var construction = SerializerFactoryGenerator.CreateRpcConstructionRoot(new GeneratorServices(compilation, new CodeGeneratorOptions()),
            list, TestContext.Current.CancellationToken);
        var exercise = $$"""
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using Orleans.Serialization;
            using Orleans.Serialization.Configuration;
            using Orleans.Serialization.Serializers;
            public sealed class ProxyContext : TypeManifestProviderBase
            {
                protected override void ConfigureInner(TypeManifestOptions options)
                {
                    {{construction.ConfigurationStatements}}
                }
            }
            public static class ProxyProof
            {
                public static bool Run()
                {
                    using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(new ProxyContext())).BuildServiceProvider();
                    var provider = services.GetRequiredService<CodecProvider>();
                    var pool = services.GetRequiredService<Orleans.Serialization.Cloning.CopyContextPool>();
                    var proxy = new OrleansCodeGen.TestProject.Proxy_IContract(provider, pool);
                    var copier = provider.GetDeepCopier<Tuple<Orleans.Runtime.SiloAddress, DateTime>>();
                    var input = Tuple.Create(Orleans.Runtime.SiloAddress.New(System.Net.IPAddress.Loopback, 1234, 1), new DateTime(638000000000000000L, DateTimeKind.Utc));
                    var copy = new DeepCopier<Tuple<Orleans.Runtime.SiloAddress, DateTime>>(copier, pool).Copy(input);
                    return proxy is TestProject.IContract && ReferenceEquals(input, copy)
                        && ReferenceEquals(copier, provider.GetDeepCopier<Tuple<Orleans.Runtime.SiloAddress, DateTime>>());
                }
            }
            """;
        var output = compilation.AddReferences(
                MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ServiceProvider).Assembly.Location))
            .AddSyntaxTrees(generated.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText, path: item.HintName)))
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(exercise, cancellationToken: TestContext.Current.CancellationToken));
        using var image = new System.IO.MemoryStream();
        var emit = output.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        Assert.Equal(true, assembly.GetType("ProxyProof")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public async Task RpcResponseFactoriesPreserveGenericJitGenerationAndExplicitValidationOverride()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IResponses<T> : IGrainWithIntegerKey
            {
                Task<T> Generic();
                Task<int> Concrete();
            }
            """);
        var jit = RunSourceGenerator(compilation);
        var overridden = RunSourceGenerator(compilation, new Dictionary<string, string>
        {
            ["build_property.publishaot"] = "true",
            ["build_property.orleansvalidaterpcresponsefactories"] = "false",
        });
        Assert.Empty(jit.Diagnostics);
        Assert.Empty(overridden.Diagnostics);
        Assert.Contains("Response<int>", ConcatenateGeneratedSources(jit));
        Assert.DoesNotContain("Response<T>", ConcatenateGeneratedSources(jit));
        Assert.Equal(ConcatenateGeneratedSources(jit), ConcatenateGeneratedSources(overridden));
    }

    [Fact]
    public async Task RpcResponseFactoriesResolveInheritedClosedGenericResultsAndCompletionMethods()
    {
        var compilation = await CreateCompilation("""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IParent<T> : IGrainWithIntegerKey { Task<T> Get(); }
            public interface IChild : IParent<int>
            {
                Task Done();
                ValueTask DoneValueTask();
                void OneWay();
            }
            """);
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.GeneratedSources, static source => source.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("Response<int>", source);
        Assert.DoesNotContain("Response<global::System.Threading.Tasks.Task", source);
        Assert.DoesNotContain("Response<void>", source);
    }

    [Theory]
    [InlineData("Task<int>")]
    [InlineData("ValueTask<int>")]
    public async Task RpcResponseHoldersGenerateDirectPrimitiveWritesAndCopiedInvocations(string returnType)
    {
        var compilation = await CreateCompilation($$"""
            using Orleans;
            using System.Threading.Tasks;
            namespace TestProject;
            public interface IWriter : IGrainWithIntegerKey { {{returnType}} Get(); }
            """);
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var response = Assert.Single(result.GeneratedSources, static item => item.HintName.EndsWith(".orleans.rpcresponses.g.cs", StringComparison.Ordinal)).SourceText.ToString();
        var proxy = Assert.Single(result.GeneratedSources, static item => item.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.Contains("IRawResponseWriter", response);
        Assert.Contains("IRawResponseReader", response);
        Assert.Contains("Int32Codec.WriteField(ref writer, 0, Value)", response);
        Assert.Contains("ResponsePool.GetGenerated<", response);
        Assert.Contains("_factory = null", response);
        Assert.Contains("options.AddRawResponseReader<int>", response);
        Assert.Contains("IResponseInvokable.InvokeAndCopy", proxy);
        Assert.Contains("factory.RentCopied(value, contexts)", proxy);
        Assert.DoesNotContain("MakeGenericType", response);
        var holders = CSharpSyntaxTree.ParseText(response, cancellationToken: TestContext.Current.CancellationToken)
            .GetCompilationUnitRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Where(static type => type.BaseList?.ToString().Contains("IRawResponseWriter", StringComparison.Ordinal) == true).ToArray();
        var holder = Assert.Single(holders);
        Assert.Null(holder.TypeParameterList);
        var output = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Options.IConfigureOptions<>).Assembly.Location))
            .AddSyntaxTrees(result.GeneratedSources.Select(static item => CSharpSyntaxTree.ParseText(item.SourceText,
                options: new CSharpParseOptions().WithPreprocessorSymbols("NET5_0_OR_GREATER"), path: item.HintName)));
        Assert.Empty(output.GetDiagnostics(TestContext.Current.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task RpcResponseHoldersKeepCustomAndGenericInvokerContracts()
    {
        var compilation = await CreateCompilation("""
            using System;
            using System.Threading.Tasks;
            using Orleans;
            using Orleans.Runtime;
            namespace TestProject;
            [InvokableBaseType(typeof(GrainReference), typeof(Task<>), typeof(CustomRequest<>))]
            [AttributeUsage(AttributeTargets.Method)]
            public sealed class CustomAttribute : Attribute { }
            public abstract class CustomRequest<T> : TaskRequest<T> { }
            public interface ICompatibility : IGrainWithIntegerKey
            {
                [Custom] Task<int> Custom();
                Task<int> Generic<T>();
            }
            """);
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);
        var proxy = Assert.Single(result.GeneratedSources, static item => item.HintName.Contains(".orleans.proxy.", StringComparison.Ordinal)).SourceText.ToString();
        Assert.DoesNotContain("IResponseInvokable", proxy);
    }

    private static GeneratorRunResult RunSourceGenerator(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, string>? globalOptions = null)
    {
        var generator = new OrleansSerializationSourceGenerator().AsSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            optionsProvider: TestCompilationHelper.CreateOptionsProvider(globalOptions),
            driverOptions: new GeneratorDriverOptions(default));
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Results.Single();
    }

    /// <summary>
    /// Helper method that runs the Orleans source generator on the provided code
    /// and verifies successful generation without errors.
    /// Uses snapshot testing to verify the generated code matches expectations.
    /// </summary>
    private static async Task AssertSuccessfulSourceGeneration(string code, string? snapshotName = null)
    {
        var projectName = "TestProject";
        var compilation = await CreateCompilation(code, projectName);
        Assert.Empty(compilation.GetDiagnostics());
        var result = RunSourceGenerator(compilation);
        Assert.Empty(result.Diagnostics);

        Assert.NotEmpty(result.GeneratedSources);
        Assert.All(result.GeneratedSources, generated =>
            Assert.StartsWith($"{projectName}.orleans.", generated.HintName, StringComparison.Ordinal));
        var generatedSource = ConcatenateGeneratedSources(result);

        var snapshot = Verify(generatedSource, extension: "cs").UseDirectory("snapshots");
        if (snapshotName is not null)
        {
            var supportsGenericAccessors = SourceGeneratorOptionsParser.ParseOptions(TestCompilationHelper.CreateOptionsProvider().GlobalOptions).SupportsGenericUnsafeAccessors;
            snapshot = snapshot.UseFileName($"{nameof(OrleansSourceGeneratorTests)}.{snapshotName}.{(supportsGenericAccessors ? "UnsafeAccessor" : "FieldAccessor")}");
        }
        if (generatedSource.Contains("global::Orleans.Serialization.Invocation.IRawResponseWriter", StringComparison.Ordinal))
        {
            snapshot = snapshot.UniqueForRuntimeAndVersion();
        }

        await snapshot;
    }

    private static string ConcatenateGeneratedSources(GeneratorRunResult result)
    {
        var assemblyAttributes = new List<AttributeListSyntax>();
        var topLevelMembers = new List<MemberDeclarationSyntax>();
        var namespaces = new Dictionary<string, NamespaceMembers>(StringComparer.Ordinal);

        foreach (var item in result.GeneratedSources
            .Select(static (source, index) => (Source: source, Index: index))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Source.SourceText.ToString()))
            .OrderBy(static item => GetSnapshotSourceOrder(item.Source.HintName))
            .ThenBy(static item => item.Index))
        {
            var root = CSharpSyntaxTree.ParseText(item.Source.SourceText.ToString().TrimStart('\uFEFF').TrimEnd()).GetCompilationUnitRoot();
            assemblyAttributes.AddRange(root.AttributeLists);

            foreach (var member in root.Members)
            {
                if (member is NamespaceDeclarationSyntax namespaceDeclaration)
                {
                    var namespaceName = namespaceDeclaration.Name.ToString();
                    if (!namespaces.TryGetValue(namespaceName, out var namespaceMembers))
                    {
                        namespaceMembers = new NamespaceMembers(namespaceDeclaration.Usings);
                        namespaces.Add(namespaceName, namespaceMembers);
                    }

                    namespaceMembers.Members.AddRange(namespaceDeclaration.Members);
                }
                else
                {
                    topLevelMembers.Add(member);
                }
            }
        }

        var combinedMembers = new List<MemberDeclarationSyntax>();
        combinedMembers.AddRange(topLevelMembers);
        foreach (var pair in namespaces.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            combinedMembers.Add(
                SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(pair.Key))
                    .WithUsings(pair.Value.Usings)
                    .WithMembers(SyntaxFactory.List(OrderLegacyGeneratedMembers(pair.Value.Members))));
        }

        var unit = SyntaxFactory.CompilationUnit()
            .WithAttributeLists(SyntaxFactory.List(assemblyAttributes))
            .WithMembers(SyntaxFactory.List(combinedMembers));
        var resultText = unit.NormalizeWhitespace().ToFullString();
        resultText = resultText.Replace(".Add(typeof(int));", ".Add(typeof( int ));", StringComparison.Ordinal);
        if (assemblyAttributes.Count > 0)
        {
            resultText += $"{Environment.NewLine}#pragma warning restore";
        }

        return resultText;

        static int GetSnapshotSourceOrder(string hintName)
        {
            if (hintName.Contains(".orleans.proxy.", StringComparison.Ordinal))
            {
                return 0;
            }

            if (hintName.Contains(".orleans.ser.", StringComparison.Ordinal))
            {
                return 1;
            }

            if (hintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal))
            {
                return 3;
            }

            return 2;
        }
    }

    private static List<MemberDeclarationSyntax> OrderLegacyGeneratedMembers(List<MemberDeclarationSyntax> members)
    {
        var serializerOrder = GetSerializerOrder(members);
        return members
            .Select(static (member, index) => (Member: member, Index: index))
            .OrderBy(static item => GetLegacyMemberCategory(item.Member))
            .ThenBy(item => GetSerializerOrderIndex(item.Member, serializerOrder))
            .ThenBy(static item => GetSerializerMemberKindOrder(item.Member))
            .ThenBy(static item => item.Index)
            .Select(static item => item.Member)
            .ToList();
    }

    private static Dictionary<string, int> GetSerializerOrder(List<MemberDeclarationSyntax> members)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var metadataClass in members
            .OfType<ClassDeclarationSyntax>()
            .Where(static declaration => declaration.Identifier.ValueText.StartsWith("Metadata_", StringComparison.Ordinal)))
        {
            foreach (var invocation in metadataClass.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "AddSerializer"
                    }
                    || invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not TypeOfExpressionSyntax typeOfExpression)
                {
                    continue;
                }

                var serializerClassName = GetGeneratedClassIdentifier(typeOfExpression.Type.ToString().Split('.').Last());
                if (serializerClassName.StartsWith("Codec_", StringComparison.Ordinal))
                {
                    result.TryAdd(serializerClassName["Codec_".Length..], result.Count);
                }
            }
        }

        return result;
    }

    private static string GetGeneratedClassIdentifier(string typeName)
    {
        var genericMarkerIndex = typeName.IndexOf('<');
        return genericMarkerIndex >= 0 ? typeName[..genericMarkerIndex] : typeName;
    }

    private static int GetLegacyMemberCategory(MemberDeclarationSyntax member)
    {
        if (member is not ClassDeclarationSyntax classDeclaration)
        {
            return 1;
        }

        var className = classDeclaration.Identifier.ValueText;
        if (className.StartsWith("Invokable_", StringComparison.Ordinal)
            || className.StartsWith("Proxy_", StringComparison.Ordinal))
        {
            return 0;
        }

        if (className.StartsWith("Metadata_", StringComparison.Ordinal))
        {
            return 3;
        }

        return 1;
    }

    private static int GetSerializerOrderIndex(MemberDeclarationSyntax member, Dictionary<string, int> serializerOrder)
    {
        if (member is not ClassDeclarationSyntax classDeclaration)
        {
            return int.MaxValue;
        }

        var className = classDeclaration.Identifier.ValueText;
        var serializableTypeName = GetSerializableTypeName(className);
        if (serializableTypeName is not null && serializerOrder.TryGetValue(serializableTypeName, out var order))
        {
            return order;
        }

        return int.MaxValue;
    }

    private static int GetSerializerMemberKindOrder(MemberDeclarationSyntax member)
    {
        if (member is not ClassDeclarationSyntax classDeclaration)
        {
            return 0;
        }

        var className = classDeclaration.Identifier.ValueText;
        if (className.StartsWith("Codec_", StringComparison.Ordinal))
        {
            return 0;
        }

        if (className.StartsWith("Copier_", StringComparison.Ordinal))
        {
            return 1;
        }

        if (className.StartsWith("Activator_", StringComparison.Ordinal))
        {
            return 2;
        }

        return 0;
    }

    private static string? GetSerializableTypeName(string generatedClassName)
    {
        if (generatedClassName.StartsWith("Codec_", StringComparison.Ordinal))
        {
            return generatedClassName["Codec_".Length..];
        }

        if (generatedClassName.StartsWith("Copier_", StringComparison.Ordinal))
        {
            return generatedClassName["Copier_".Length..];
        }

        if (generatedClassName.StartsWith("Activator_", StringComparison.Ordinal))
        {
            return generatedClassName["Activator_".Length..];
        }

        return null;
    }

    private static int CountGeneratedInvokableClasses(string source)
        => source.Split(Environment.NewLine)
            .Count(line => line.Contains("public sealed class Invokable_", StringComparison.Ordinal));

    private static int CountOccurrences(string value, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }

    private static string[] GetGeneratedClassNames(GeneratorRunResult result, string hintNameFragment, string classNamePrefix)
        => result.GeneratedSources
            .Where(source => source.HintName.Contains(hintNameFragment, StringComparison.Ordinal))
            .SelectMany(static source => CSharpSyntaxTree.ParseText(source.SourceText.ToString().TrimStart('\uFEFF')).GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            .Select(static declaration => declaration.Identifier.ValueText)
            .Where(name => name.StartsWith(classNamePrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private static string[] GetRegisteredGeneratedInvokableActivatorNames(GeneratorRunResult result)
        => result.GeneratedSources
            .Where(static source => source.HintName.EndsWith(".orleans.metadata.g.cs", StringComparison.Ordinal))
            .SelectMany(static source => CSharpSyntaxTree.ParseText(source.SourceText.ToString().TrimStart('\uFEFF')).GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            .Where(static invocation =>
                invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "AddActivator"
                })
            .Select(static invocation => invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression)
            .OfType<TypeOfExpressionSyntax>()
            .Select(static typeOfExpression => GetGeneratedClassIdentifier(typeOfExpression.Type.ToString().Split('.').Last()))
            .Where(static name => name.StartsWith("Activator_Invokable_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private sealed class NamespaceMembers(SyntaxList<UsingDirectiveSyntax> usings)
    {
        public SyntaxList<UsingDirectiveSyntax> Usings { get; } = usings;

        public List<MemberDeclarationSyntax> Members { get; } = [];
    }

    /// <summary>
    /// Creates a Roslyn compilation with the necessary Orleans references.
    /// This simulates the build environment where the source generator runs,
    /// including all required Orleans assemblies and .NET framework references.
    /// </summary>
    private static Task<CSharpCompilation> CreateCompilation(string sourceCode, string assemblyName = "TestProject")
        => TestCompilationHelper.CreateCompilation(sourceCode, assemblyName);
}
