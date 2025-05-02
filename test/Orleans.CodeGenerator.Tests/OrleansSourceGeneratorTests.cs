using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGenerator.Diagnostics;
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

    [GeneratedActivatorConstructor]
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

    [OrleansConstructor]
    public DerivedData(string baseValue, string derivedValue) : base(baseValue)
    {
        DerivedValue = derivedValue;
    }
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
}");

    [Fact]
    public Task TestClassWithNoPublicConstructors() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

[GenerateSerializer]
public class NoPublicCtor
{
    [OrleansConstructor]
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
    public Task TestClassesWithOrleansConstructorAnnotation() => AssertSuccessfulSourceGeneration(
@"using Orleans;

namespace TestProject;

public class ClassWithOrleansConstructor
{
    [Id(0)]
    public int Value { get; set; }

    [Id(1)]
    public string Name { get; set; } = string.Empty;

    [OrleansConstructor]
    public ClassWithOrleansConstructor(int value, string name)
    {
        Value = value;
        Name = name;
    }

    public ClassWithOrleansConstructor() { }
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
    public string StringValue { get; }
    public MyCustomEnum EnumValue { get; }

    [OrleansConstructor]
    public ClassWithImplicitFieldIds(string stringValue, MyCustomEnum enumValue)
    {
        StringValue = stringValue;
        EnumValue = enumValue;
    }
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
        var attrList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(referenceAssemblyAttribute));
        var assemblyAttr = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(referenceAssemblyAttribute))
            .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.AssemblyKeyword)));
        var root = (CSharpSyntaxNode)compilation.SyntaxTrees[0].GetRoot();
        var newRoot = ((CompilationUnitSyntax)root).AddAttributeLists(assemblyAttr);
        var newTree = compilation.SyntaxTrees[0].WithRootAndOptions(newRoot, compilation.SyntaxTrees[0].Options);

        // leave only syntaxTree with the ReferenceAssemblyAttribute
        compilation = compilation.RemoveSyntaxTrees(compilation.SyntaxTrees[0]).AddSyntaxTrees(newTree);

        var generator = new OrleansSerializationSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [generator],
            driverOptions: new GeneratorDriverOptions(default));
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult().Results.Single();
        Assert.Contains(result.Diagnostics, d => d.Id == DiagnosticRuleId.ReferenceAssemblyWithGenerateSerializer);
    }

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
