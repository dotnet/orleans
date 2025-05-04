using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.CodeGenerator.Tests;

public class OrleansSourceGeneratorTests
{
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

[Fact]
    public Task TestBasicClassWithFields() => AssertSuccessfulSourceGeneration(
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
    public Task TestBasicStruct() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public struct DemoData
{
    [Id(0)]
    public string Value { get; set; }
}");

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

    [Fact]
    public Task TestGrainComplexGrain() => AssertSuccessfulSourceGeneration(
@"using Orleans;
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
    Task<ComplexData> ProcessData(int inputInt, string inputString, ComplexData data);
}

[GenerateSerializer]
public class ComplexGrain : Grain, IComplexGrain
{
    public Task<ComplexData> ProcessData(int inputInt, string inputString, ComplexData data)
    {
        var result = new ComplexData
        {
            IntValue = inputInt * 2 + data.IntValue,
            StringValue = $""Processed: {inputString}"" + data.StringValue
        };
        return Task.FromResult(result);
    }
}");

    private static async Task AssertSuccessfulSourceGeneration(string code)
    {
        var projectName = "TestProject";
        var compilation = await CreateCompilation(code, projectName);
        Assert.Empty(compilation.GetDiagnostics());
        var generator = new OrleansSerializationSourceGenerator();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));

        // Run the generator
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results.Single();
        Assert.Empty(result.Diagnostics);

        Assert.Single(result.GeneratedSources);
        Assert.Equal($"{projectName}.orleans.g.cs", result.GeneratedSources[0].HintName);
        var generatedSource = result.GeneratedSources[0].SourceText.ToString();

        await Verify(generatedSource, extension: "cs").UseDirectory("snapshots");
    }

    private static async Task<CSharpCompilation> CreateCompilation(string sourceCode, string assemblyName = "TestProject")
    {
        var references = await ReferenceAssemblies.Net.Net80.ResolveAsync(LanguageNames.CSharp, default);

        // Add the Orleans Orleans.Core.Abstractions assembly
        references = references.AddRange(
            // Orleans.Core.Abstractions
            MetadataReference.CreateFromFile(typeof(GrainId).Assembly.Location),
            // Orleans.Core
            MetadataReference.CreateFromFile(typeof(IClusterClientLifecycle).Assembly.Location),
            // Orleans.Runtime
            MetadataReference.CreateFromFile(typeof(IGrainActivator).Assembly.Location),
            // Orleans.Serialization
            MetadataReference.CreateFromFile(typeof(Serializer).Assembly.Location),
            // Orleans.Serialization.Abstractions
            MetadataReference.CreateFromFile(typeof(GenerateFieldIds).Assembly.Location),
            // Microsoft.Extensions.DependencyInjection.Abstractions
            MetadataReference.CreateFromFile(typeof(ActivatorUtilitiesConstructorAttribute).Assembly.Location)
        );

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        return CSharpCompilation.Create(assemblyName, [syntaxTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
