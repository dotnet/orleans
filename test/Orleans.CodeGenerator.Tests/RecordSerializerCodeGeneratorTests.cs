using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.CodeGenerator.Tests;

public class RecordSerializerCodeGeneratorTests : CodeGeneratorTestBase
{

    public RecordSerializerCodeGeneratorTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    [Fact]
    public void RoundtripRecordWithExplicitlyMarkedPrimaryCtorArgument()
    {
        var generatedCode = GenerateCodeFrom("""
            [GenerateSerializer]
            record Test([property: Id(1000)]int A);
            """);

        var resultSourceText = generatedCode.ToFullString();
        Assert.Equal(_expectedGeneratedCode, resultSourceText);
    }

    [Fact]
    public void RoundtripRecordWithExplicitlyMarkedProperty()
    {
        var generatedCode = GenerateCodeFrom("""
            [GenerateSerializer]
            record Test(int A){
                [Id(1000)]int A { get; init; } 
            }
            """);

        var resultSourceText = generatedCode.ToFullString();
        Assert.Equal(_expectedGeneratedCode, resultSourceText);
    }

    [Fact]
    public void RoundtripRecordWithImplicitlyMarkedProperty()
    {
        var generatedCode = GenerateCodeFrom("""
            [GenerateSerializer]
            record Test(int A);
            """);

        var resultSourceText = generatedCode.ToFullString();
        Assert.Equal(_expectedGeneratedCode, resultSourceText);
    }


    [Fact]
    public void GenerateCode_IntermixedRecord_IncludesAllMembers()
    {
        var generatedCode = GenerateCodeFrom("""
            [GenerateSerializer]
            record Test(int A, int B, [property: Id(2)]int C) {
                [Id(1)] public int B { get; init; }
            };
            """);

        var generatedSerializableMembers = EnumerateGeneratedSerializableMembers(generatedCode).ToList();

        Assert.Contains(("1000u", "A"), generatedSerializableMembers);
        Assert.Contains(("1u", "B"), generatedSerializableMembers);
        Assert.Contains(("2u", "C"), generatedSerializableMembers);
    }

    [Fact]
    public void GenerateCode_WithGenerateFieldIds_DoesNotIncludePrimaryConstructorProperties()
    {
        var codeGeneratorOptions = new CodeGeneratorOptions { GenerateFieldIds = true };
        var generatedCode = GenerateCodeFrom("""
            [GenerateSerializer]
            record Test(int A);
            """);

        var generatedSerializableMembers = EnumerateGeneratedSerializableMembers(generatedCode).ToList();

        Assert.Contains(("0", "A"), generatedSerializableMembers);
        Assert.DoesNotContain(("1000u", "A"), generatedSerializableMembers);
    }

    IEnumerable<(string indexExpression, string fieldExpression)> EnumerateGeneratedSerializableMembers(CompilationUnitSyntax generatedCode)
    {
        return generatedCode
            .DescendantNodes()
            .Where(x => x is ClassDeclarationSyntax { Identifier.Text: "Codec_Test" })
            .SelectMany(x => x.DescendantNodes())
            .Where(x => x is MethodDeclarationSyntax { Identifier.Text: "Serialize" })
            .SelectMany(x => x.DescendantNodes())
            .OfType<InvocationExpressionSyntax>()
            .Select(x =>
            (
                index: x.ArgumentList.Arguments.Skip(1).First().Expression.ToString(),
                member: x.ArgumentList.Arguments.Skip(3).First().DescendantNodes().OfType<IdentifierNameSyntax>().Last().ToString()
            ));
    }

    const string _expectedGeneratedCode = """
        [assembly: global::Orleans.ApplicationPartAttribute("compilation")]
        [assembly: global::Orleans.ApplicationPartAttribute("Orleans.Serialization")]
        [assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof(OrleansCodeGen.compilation.Metadata_compilation))]
        namespace OrleansCodeGen
        {
            using global::Orleans.Serialization.Codecs;
            using global::Orleans.Serialization.GeneratedCodeHelpers;

            [System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "4.0.0.0")]
            internal sealed class Codec_Test : global::Orleans.Serialization.Codecs.IFieldCodec<global::Test>, global::Orleans.Serialization.Serializers.IBaseCodec<global::Test>
            {
                private static readonly global::System.Type _int32Type = typeof(int);
                private static readonly global::System.Type _codecFieldType = typeof(global::Test);
                private readonly global::Orleans.Serialization.Activators.IActivator<global::Test> _activator;
                private readonly global::System.Action<global::Test, int> setField0;
                public Codec_Test(global::Orleans.Serialization.Activators.IActivator<global::Test> _activator, global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
                {
                    this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
                    setField0 = (global::System.Action<global::Test, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::Test).GetField("<A>k__BackingField", (global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Public)));
                }

                [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::Test instance)
                    where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
                {
                    global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1000U, _int32Type, instance.A);
                }

                [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Test instance)
                {
                    int id = 0;
                    global::Orleans.Serialization.WireProtocol.Field header = default;
                    while (true)
                    {
                        id = OrleansGeneratedCodeHelper.ReadHeader(ref reader, ref header, id);
                        if (id == 1000)
                        {
                            setField0(instance, (int)global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header));
                            id = OrleansGeneratedCodeHelper.ReadHeaderExpectingEndBaseOrEndObject(ref reader, ref header, id);
                        }

                        if (id == -1)
                        {
                            break;
                        }

                        reader.ConsumeUnknownField(header);
                    }
                }

                [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Test @value)
                    where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
                {
                    global::System.Type valueType = @value?.GetType();
                    if (valueType is null || valueType == _codecFieldType)
                    {
                        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, @value))
                        {
                            return;
                        }

                        writer.WriteStartObject(fieldIdDelta, expectedType, valueType);
                        this.Serialize(ref writer, @value);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        OrleansGeneratedCodeHelper.SerializeUnexpectedType(ref writer, fieldIdDelta, expectedType, @value);
                    }
                }

                [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                public global::Test ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
                {
                    if (field.WireType == global::Orleans.Serialization.WireProtocol.WireType.Reference)
                    {
                        return ReferenceCodec.ReadReference<global::Test, TReaderInput>(ref reader, field);
                    }

                    global::System.Type valueType = field.FieldType;
                    if (valueType is null || valueType == _codecFieldType)
                    {
                        global::Test result = _activator.Create();
                        ReferenceCodec.RecordObject(reader.Session, result);
                        this.Deserialize(ref reader, result);
                        return result;
                    }

                    return OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TReaderInput, global::Test>(ref reader, field);
                }
            }

            [System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "4.0.0.0")]
            internal sealed class Copier_Test : global::Orleans.Serialization.Cloning.IDeepCopier<global::Test>, global::Orleans.Serialization.Cloning.IBaseCopier<global::Test>
            {
                [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                public global::Test DeepCopy(global::Test original, global::Orleans.Serialization.Cloning.CopyContext context)
                {
                    if (context.TryGetCopy(original, out global::Test result))
                        return result;
                    if (original.GetType() != typeof(global::Test))
                        return context.DeepCopy(original);
                    result = _activator.Create();
                    context.RecordCopy(original, result);
                    setField0(result, original.A);
                    return result;
                }

                private readonly global::Orleans.Serialization.Activators.IActivator<global::Test> _activator;
                private readonly global::System.Action<global::Test, int> setField0;
                public Copier_Test(global::Orleans.Serialization.Activators.IActivator<global::Test> _activator, global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
                {
                    this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
                    setField0 = (global::System.Action<global::Test, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::Test).GetField("<A>k__BackingField", (global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Public)));
                }

                [System.Runtime.CompilerServices.MethodImplAttribute(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                public void DeepCopy(global::Test input, global::Test output, global::Orleans.Serialization.Cloning.CopyContext context)
                {
                    setField0(output, input.A);
                }
            }
        }

        namespace OrleansCodeGen.compilation
        {
            using global::Orleans.Serialization.Codecs;
            using global::Orleans.Serialization.GeneratedCodeHelpers;

            [System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "4.0.0.0")]
            internal sealed class Metadata_compilation : global::Orleans.Serialization.Configuration.ITypeManifestProvider
            {
                public void Configure(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
                {
                    config.Serializers.Add(typeof(OrleansCodeGen.Codec_Test));
                    config.Copiers.Add(typeof(OrleansCodeGen.Copier_Test));
                }
            }
        }
        """;
}

