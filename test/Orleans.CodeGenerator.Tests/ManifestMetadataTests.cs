using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator.Tests;

public class ManifestMetadataTests
{
    private const string Source = """
        using System;
        using System.Buffers;
        using Orleans;
        using Orleans.Serialization.Activators;
        using Orleans.Serialization.Buffers;
        using Orleans.Serialization.Cloning;
        using Orleans.Serialization.Serializers;

        namespace MetadataTargets;

        public class Outer<T>
        {
            public class Nested<TItem> : IComparable<Tuple<TItem, T>>
            {
                public int CompareTo(Tuple<TItem, T> other) => 0;
            }
        }

        public class Target<T> { }
        public struct Surrogate<T> { }

        [RegisterSerializer]
        public class Codec<T> : IBaseCodec<Outer<T>.Nested<Target<int>>>
        {
            public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, Outer<T>.Nested<Target<int>> value)
                where TBufferWriter : IBufferWriter<byte> { }
            public void Deserialize<TInput>(ref Reader<TInput> reader, Outer<T>.Nested<Target<int>> value) { }
        }

        [RegisterCopier]
        public class Copier<T> : ShallowCopier<Outer<T>.Nested<Target<int>>> { }

        [RegisterActivator]
        public class Activator<T> : IActivator<Outer<T>.Nested<Target<int>>>
        {
            public Outer<T>.Nested<Target<int>> Create() => new();
        }

        [RegisterConverter]
        public class Converter<T> : IConverter<Outer<T>.Nested<Target<int>>, Surrogate<Target<T>[]>>
        {
            public Outer<T>.Nested<Target<int>> ConvertFromSurrogate(in Surrogate<Target<T>[]> value) => new();
            public Surrogate<Target<T>[]> ConvertToSurrogate(in Outer<T>.Nested<Target<int>> value) => default;
        }

        [GenerateSerializer]
        public class Generated<T> : IComparable<Outer<T>.Nested<Target<int>>>
        {
            public int CompareTo(Outer<T>.Nested<Target<int>> other) => 0;
        }
        """;

    [Theory]
    [InlineData("Codec")]
    [InlineData("Copier")]
    [InlineData("Activator")]
    [InlineData("Converter")]
    public async Task RegisteredImplementationsDescribeTheirTargetContracts(string name)
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        var symbol = compilation.GetTypeByMetadataName($"MetadataTargets.{name}`1");
        Assert.NotNull(symbol);

        var kind = name switch
        {
            "Codec" => RegisteredCodecKind.Serializer,
            "Copier" => RegisteredCodecKind.Copier,
            "Activator" => RegisteredCodecKind.Activator,
            "Converter" => RegisteredCodecKind.Converter,
            _ => throw new InvalidOperationException($"Unexpected registration: {name}.")
        };
        var model = ModelExtractor.ExtractRegisteredCodec(symbol, kind, compilation);
        var contract = Assert.Single(model.Contracts);
        Assert.Equal("global::MetadataTargets.Outer<>.Nested<>", contract.Target.Type.SyntaxString);
        Assert.True(contract.Target.IsAccessible);
        Assert.NotNull(contract.TargetDescription);
        Assert.Equal(name switch
        {
            "Codec" => "AddBaseCodec",
            "Copier" => "AddCopier",
            "Activator" => "AddActivator",
            _ => "AddConverter"
        }, contract.RegistrationMethod);

        if (kind == RegisteredCodecKind.Converter)
        {
            Assert.Equal("global::MetadataTargets.Surrogate<>", contract.Surrogate?.Type.SyntaxString);
        }
    }

    [Fact]
    public async Task GeneratedManifestEmitsDeterministicTargetRegistrations()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        var result = RunGenerator(compilation, out var updatedCompilation);
        Assert.Empty(updatedCompilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var metadata = GetMetadata(result);
        var source = metadata.ToString();
        Assert.Contains("config.AddSerializationContract(typeof(global::MetadataTargets.Codec<>), typeof(global::Orleans.Serialization.Serializers.IBaseCodec<>),", source);
        var converterRegistration = Assert.Single(metadata.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "config.AddSerializationContract"
                && invocation.ArgumentList.Arguments[0].ToString().Contains("MetadataTargets.Converter<>", StringComparison.Ordinal));
        var converterInterface = Assert.IsType<TypeOfExpressionSyntax>(converterRegistration.ArgumentList.Arguments[1].Expression);
        Assert.Equal("global::Orleans.IConverter<,>", converterInterface.Type.ToString().Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Contains("SerializationType.Array", source);
        Assert.Contains("SerializationType.Parameter(0)", source);
        Assert.Contains("typeof(global::MetadataTargets.Generated<>)", source);
        Assert.DoesNotContain("PreserveTypeMetadata", source);

        var reordered = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Source.Replace(
                "[RegisterSerializer]", "[RegisterSerializer]\n", StringComparison.Ordinal),
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(metadata.ToString(), GetMetadata(RunGenerator(reordered, out _)).ToString());
    }

    [Fact]
    public async Task ChangingImplementedInterfaceInvalidatesMetadataModel()
    {
        var compilation = await TestCompilationHelper.CreateCompilation(Source);
        var symbol = compilation.GetTypeByMetadataName("MetadataTargets.Codec`1");
        Assert.NotNull(symbol);
        var original = ModelExtractor.ExtractRegisteredCodec(symbol, RegisteredCodecKind.Serializer, compilation);

        var changedCompilation = await TestCompilationHelper.CreateCompilation(
            Source.Replace("Outer<T>.Nested<Target<int>>", "Target<int>", StringComparison.Ordinal));
        var changedSymbol = changedCompilation.GetTypeByMetadataName("MetadataTargets.Codec`1");
        Assert.NotNull(changedSymbol);
        var changed = ModelExtractor.ExtractRegisteredCodec(changedSymbol, RegisteredCodecKind.Serializer, changedCompilation);

        Assert.NotEqual(original, changed);
        Assert.Equal("global::MetadataTargets.Target<int>", Assert.Single(changed.Contracts).Target.Type.SyntaxString);
    }

    [Fact]
    public async Task InaccessibleTargetRegistrationsResolveTheirAssemblyQualifiedType()
    {
        const string source = """
            using Orleans;
            using Orleans.Serialization.Cloning;
            public class Container
            {
                private class Hidden<T> { }
                [RegisterCopier]
                internal class Copier : IDeepCopier<Hidden<int>>
                {
                    Hidden<int> IDeepCopier<Hidden<int>>.DeepCopy(Hidden<int> input, CopyContext context) => input;
                }
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var result = RunGenerator(compilation, out var updated);

        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("global::System.Type.GetType(\"Container+Hidden`1[[System.Int32, System.Private.CoreLib]], TestProject\", true)", GetMetadata(result).ToString());
        Assert.DoesNotContain("PreserveTypeMetadata", GetMetadata(result).ToString());
    }

    [Fact]
    public async Task ParameterizedArrayContractsEmitTheirGenericParameterDescription()
    {
        const string source = """
            using System;
            using System.Buffers;
            using Orleans;
            using Orleans.Serialization.Buffers;
            using Orleans.Serialization.Codecs;
            using Orleans.Serialization.WireProtocol;
            [RegisterSerializer]
            public class Codec<T> : IFieldCodec<T[]>
            {
                public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, Type expected, T[] value)
                    where TBufferWriter : IBufferWriter<byte> { }
                public T[] ReadValue<TInput>(ref Reader<TInput> reader, Field field) => default;
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var result = RunGenerator(compilation, out var updated);

        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var metadata = GetMetadata(result).ToString();
        Assert.Contains("config.AddSerializationContract(typeof(global::Codec<>), typeof(global::Orleans.Serialization.Codecs.IFieldCodec<>),", metadata);
        Assert.Contains("SerializationType.Array(global::Orleans.Serialization.Configuration.SerializationType.Parameter(0), 1)", metadata);
        Assert.DoesNotContain("config.AddSerializer(typeof(global::Codec<>))", metadata);
    }

    [Theory]
    [InlineData("dynamic", "object")]
    [InlineData("List<dynamic>", "global::System.Collections.Generic.List<object>")]
    [InlineData("Dictionary<string, List<dynamic[]>>", "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<object[]>>")]
    [InlineData("(dynamic, List<dynamic>)", "(object, global::System.Collections.Generic.List<object>)")]
    [InlineData("global::DynamicTargets.Escaped.@dynamic", "global::DynamicTargets.Escaped.dynamic")]
    public async Task DynamicContractTargetsEmitObjectTypeSyntax(string target, string expected)
    {
        var source = $$"""
            using System;
            using System.Buffers;
            using System.Collections.Generic;
            using Orleans;
            using Orleans.Serialization.Buffers;
            using Orleans.Serialization.Codecs;
            using Orleans.Serialization.WireProtocol;
            namespace DynamicTargets;
            public class Escaped
            {
                public class @dynamic { }
            }
            public abstract class CodecBase<T> : IFieldCodec<T>
            {
                public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint id, Type expected, T value)
                    where TBufferWriter : IBufferWriter<byte> { }
                public T ReadValue<TInput>(ref Reader<TInput> reader, Field field) => default;
            }
            [RegisterSerializer]
            public class Codec : CodecBase<{{target}}> { }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var symbol = compilation.GetTypeByMetadataName("DynamicTargets.Codec");
        Assert.NotNull(symbol);
        var contract = Assert.Single(ModelExtractor.ExtractRegisteredCodec(symbol, RegisteredCodecKind.Serializer, compilation).Contracts);
        Assert.Equal(expected, contract.Target.Type.SyntaxString);
        var result = RunGenerator(compilation, out var updated);

        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains($"config.AddSerializer(typeof(global::DynamicTargets.Codec), typeof({expected}))", GetMetadata(result).ToString());
    }

    [Fact]
    public async Task DynamicContractsEmitObjectRuntimeTypeNames()
    {
        const string source = """
            using System.Collections.Generic;
            using Orleans;
            using Orleans.Serialization.Cloning;
            public class Container
            {
                internal class Hidden<T> { }
                [RegisterCopier]
                internal class Copier : ShallowCopier<Hidden<List<dynamic[]>>> { }
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var symbol = compilation.GetTypeByMetadataName("Container+Copier");
        Assert.NotNull(symbol);
        var contract = Assert.Single(ModelExtractor.ExtractRegisteredCodec(symbol, RegisteredCodecKind.Copier, compilation).Contracts);
        var result = RunGenerator(compilation, out var updated);

        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Equal("Container+Hidden`1[[System.Collections.Generic.List`1[[System.Object[], System.Private.CoreLib]], System.Private.CoreLib]], TestProject",
            contract.Target.RuntimeTypeName);
        Assert.Contains("typeof(global::Container.Hidden<global::System.Collections.Generic.List<object[]>>)", GetMetadata(result).ToString());
    }

    [Fact]
    public async Task DescribedDynamicTargetsAndSurrogatesEmitObjectNodes()
    {
        const string source = """
            using System.Collections.Generic;
            using Orleans;
            public class Target<T, TItem> { }
            public struct Surrogate<T, TItem> { }
            public abstract class ConverterBase<TValue, TSurrogate> : IConverter<TValue, TSurrogate> where TSurrogate : struct
            {
                public abstract TValue ConvertFromSurrogate(in TSurrogate value);
                public abstract TSurrogate ConvertToSurrogate(in TValue value);
            }
            [RegisterConverter]
            public class Converter<T> : ConverterBase<Target<T, List<dynamic[]>>, Surrogate<T, List<dynamic>>>
            {
                public override Target<T, List<dynamic[]>> ConvertFromSurrogate(in Surrogate<T, List<dynamic>> value) => new();
                public override Surrogate<T, List<dynamic>> ConvertToSurrogate(in Target<T, List<dynamic[]>> value) => default;
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var result = RunGenerator(compilation, out var updated);

        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var metadata = GetMetadata(result).ToString();
        Assert.Contains("SerializationType.Create(typeof(global::System.Collections.Generic.List<object[]>))", metadata);
        Assert.Contains("SerializationType.Create(typeof(global::System.Collections.Generic.List<object>))", metadata);
    }

    [Fact]
    public async Task ConcreteArrayNodesEmitSourceKnownTypesInsideGenericContracts()
    {
        const string source = """
            using Orleans;
            public class Target<T, TArray> { }
            public struct Surrogate<T, TArray> { }
            public class Box<T> { }
            [RegisterConverter]
            public class Converter<T> : IConverter<Target<T, byte[]>, Surrogate<T, Box<byte>[]>>
            {
                public Target<T, byte[]> ConvertFromSurrogate(in Surrogate<T, Box<byte>[]> value) => new();
                public Surrogate<T, Box<byte>[]> ConvertToSurrogate(in Target<T, byte[]> value) => default;
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var symbol = compilation.GetTypeByMetadataName("Converter`1");
        Assert.NotNull(symbol);
        var contract = Assert.Single(ModelExtractor.ExtractRegisteredCodec(symbol, RegisteredCodecKind.Converter, compilation).Contracts);
        Assert.Equal(0, contract.TargetDescription!.Value.Arguments[1].ArrayRank);
        Assert.Equal("byte[]", contract.TargetDescription.Value.Arguments[1].Type!.Value.Type.SyntaxString);
        Assert.Equal(0, contract.SurrogateDescription!.Value.Arguments[1].ArrayRank);
        Assert.Equal("global::Box<byte>[]", contract.SurrogateDescription.Value.Arguments[1].Type!.Value.Type.SyntaxString);
        var result = RunGenerator(compilation, out var updated);

        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var metadata = GetMetadata(result).ToString();
        Assert.Contains("SerializationType.Create(typeof(byte[]))", metadata);
        Assert.Contains("SerializationType.Create(typeof(global::Box<byte>[]))", metadata);
        Assert.DoesNotContain("SerializationType.Array", metadata);
    }

    [Fact]
    public async Task CustomInvokableBaseRegistersTheGeneratedInvokableAsItsTarget()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Orleans;
            using Orleans.Runtime;
            public interface IMetadata<T> { }
            public class OnlyInInterface<T> { }
            public abstract class CustomRequest : TaskRequest, IMetadata<OnlyInInterface<int>> { }
            [InvokableBaseType(typeof(GrainReference), typeof(Task), typeof(CustomRequest))]
            [AttributeUsage(AttributeTargets.Method)]
            public sealed class CustomRequestAttribute : Attribute { }
            public interface ICustomGrain : IGrainWithIntegerKey
            {
                [CustomRequest]
                Task Invoke();
            }
            """;
        var compilation = await TestCompilationHelper.CreateCompilation(source);
        var result = RunGenerator(compilation, out var updated);
        Assert.Empty(updated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var registration = Assert.Single(GetMetadata(result).DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "config.AddSerializer"
                && invocation.ArgumentList.Arguments[0].ToString().Contains("Codec_Invokable_", StringComparison.Ordinal));
        var target = Assert.IsType<TypeOfExpressionSyntax>(registration.ArgumentList.Arguments[1].Expression);
        Assert.Contains("Invokable_ICustomGrain_", target.Type.ToString());
        Assert.DoesNotContain("Codec_Invokable_", target.Type.ToString());
        Assert.DoesNotContain("PreserveTypeMetadata", GetMetadata(result).ToString());
    }

    private static GeneratorRunResult RunGenerator(CSharpCompilation compilation, out Compilation updated)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrleansSerializationSourceGenerator().AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out updated, out var diagnostics,
            TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics);
        return Assert.Single(driver.GetRunResult().Results);
    }

    private static CompilationUnitSyntax GetMetadata(GeneratorRunResult result)
        => Assert.Single(result.GeneratedSources.Select(static source => CSharpSyntaxTree.ParseText(source.SourceText,
                cancellationToken: TestContext.Current.CancellationToken).GetCompilationUnitRoot(TestContext.Current.CancellationToken)),
            static root => root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(static type => type.Identifier.ValueText.StartsWith("Metadata_", StringComparison.Ordinal)));
}
