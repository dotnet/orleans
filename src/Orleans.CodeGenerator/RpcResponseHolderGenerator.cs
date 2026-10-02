using System.Text;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Hashing;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class RpcResponseHolderGenerator
{
    internal static string GetName(ITypeSymbol resultType)
        => $"RpcResponse_{HexConverter.ToString(XxHash32.Hash(Encoding.UTF8.GetBytes(resultType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))))}";

    internal static string GetNamespace(Compilation compilation)
        => $"{GeneratedCodeUtilities.CodeGeneratorName}.{Identifier.SanitizeIdentifierName(compilation.AssemblyName ?? "Assembly").EscapeIdentifier()}";

    internal static bool TryDescribe(IGeneratorServices services, ITypeSymbol resultType, out string codec, out string copier)
    {
        codec = copier = "";
        if (ContainsParameter(resultType)) return false;
        if (SerializerFactoryGenerator.TryCreate(services, [resultType], CancellationToken.None, out var graph, out _))
        {
            if (graph.Registrations.Keys.OfType<INamedTypeSymbol>().Any(type =>
                SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, services.Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"))))
                return false;
            var registration = graph.Registrations[resultType.WithNullableAnnotation(NullableAnnotation.None)];
            codec = registration.Codec;
            copier = registration.Copier;
            return true;
        }

        if (resultType is INamedTypeSymbol named
            && SerializerFactoryGenerator.CreateRpcModelRoot(services, named, CancellationToken.None) is { } partial)
        {
            var registration = partial.Registrations[resultType.WithNullableAnnotation(NullableAnnotation.None)];
            if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, services.Compilation.Assembly))
            {
                var codecType = services.Compilation.GetTypeByMetadataName($"{SerializerGenerator.GetGeneratedNamespaceName(named)}.{SerializerGenerator.GetSimpleClassName(named.Name)}");
                if (codecType is null || ReferencedSerializerImplementation.Validate(codecType) is not null)
                    return false;
            }
            codec = registration.Codec;
            copier = registration.Copier;
            return true;
        }

        return false;
    }

    internal static string Generate(IGeneratorServices services, ITypeSymbol resultType, string codec, string copier)
    {
        var type = resultType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var name = GetName(resultType);
        var factory = name + "Factory";
        var shallow = services.LibraryTypes.IsShallowCopyable(resultType);
        var staticCodec = services.LibraryTypes.StaticCodecs.FindByUnderlyingType(resultType)?.CodecType;
        var staticWrite = staticCodec?.GetMembers("WriteField").OfType<IMethodSymbol>()
            .Any(static method => method.IsStatic && method.Parameters.Length == 3) == true;
        var writeResult = staticWrite
            ? $"{codec}.WriteField(ref writer, 0, value);"
            : $"_codec.WriteField(ref writer, 0, typeof({type}), value);";
        var readResult = staticCodec is not null
            ? $"{codec}.ReadValue(ref reader, field)"
            : "_codec.ReadValue(ref reader, field)";
        var staticCopier = services.LibraryTypes.StaticCopiers.FindByUnderlyingType(resultType)?.CopierType;
        var copierReceiver = staticCopier?.GetMembers("DeepCopy").OfType<IMethodSymbol>().Any(static method => method.IsStatic) == true
            ? copier : "_copier";
        var copied = shallow ? "value" : $"{copierReceiver}.DeepCopy(value, context)";
        var writeBody = resultType.IsValueType && resultType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
            ? writeResult : $"if (value is not null) {{ {writeResult} }}";
        var writeEnvelopeBody = resultType.IsValueType && resultType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
            ? writeResult.Replace("value);", "value.Value);")
            : $"if (value.Value is not null) {{ {writeResult.Replace("value);", "value.Value);")} }}";
        var holderWrite = staticWrite
            ? $"writer.WriteStartObject(0, null, typeof({type})); {writeBody.Replace("value", "Value")} writer.WriteEndObject();"
            : "_factory.WriteResult(ref writer, Value);";
        var rentCopiedBody = shallow
            ? $"return {name}.Rent(value, this);"
            : $"using var context = contexts.GetContext(); return {name}.Rent({copied}, this);";
        return $$"""
            internal sealed class {{name}} : global::Orleans.Serialization.Invocation.Response, global::Orleans.Serialization.Invocation.IRawResponseWriter
            {
                internal {{type}} Value;
                private {{factory}} _factory;
                public {{name}}() { }
                internal static {{name}} Rent({{type}} value, {{factory}} factory)
                {
                    var result = global::Orleans.Serialization.Invocation.ResponsePool.GetGenerated<{{name}}>();
                    result.Value = value;
                    result._factory = factory;
                    return result;
                }
                public override object Result { get => Value; set => Value = ({{type}})value; }
                public override global::System.Exception Exception
                {
                    get => null;
                    set => throw new global::System.InvalidOperationException("Successful response holders contain result values.");
                }
                public override global::System.Type GetSimpleResultType() => typeof({{type}});
                public override T GetResult<T>()
                {
                    if (typeof(T) == typeof({{type}}))
                        return global::System.Runtime.CompilerServices.Unsafe.As<{{type}}, T>(ref Value);
                    return (T)(object)Value;
                }
                public void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer)
                    where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
                {
                    if (_factory is null) throw new global::System.ObjectDisposedException(GetType().Name);
                    {{holderWrite}}
                }
                public override void Dispose()
                {
                    if (_factory is null) return;
                    Value = default;
                    _factory = null;
                    global::Orleans.Serialization.Invocation.ResponsePool.ReturnGenerated(this);
                }
            }

            internal sealed class {{factory}} : global::Orleans.Serialization.Invocation.ResponseCodec,
                global::Orleans.Serialization.Codecs.IFieldCodec<{{name}}>,
                global::Orleans.Serialization.Cloning.IDeepCopier<{{name}}>,
                global::Orleans.Serialization.Invocation.IRawResponseReader
            {
                private readonly {{codec}} _codec;
                private readonly {{copier}} _copier;
                public bool IsSupported { get; }
                internal static {{factory}} Resolve(global::Orleans.Serialization.Serializers.ICodecProvider provider)
                {
                    provider.GetCodec<{{type}}>();
                    provider.GetDeepCopier<{{type}}>();
                    provider.GetCodec<global::Orleans.Serialization.Invocation.Response<{{type}}>>();
                    provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<{{type}}>>();
                    return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<{{factory}}>(null, provider);
                }
                public {{factory}}(global::Orleans.Serialization.Serializers.ICodecProvider provider)
                {
                    _codec = provider.GetCodec<{{type}}>() as {{codec}};
                    _copier = provider.GetDeepCopier<{{type}}>() as {{copier}};
                    var responseCodec = provider.GetCodec<global::Orleans.Serialization.Invocation.Response<{{type}}>>();
                    var responseCopier = provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<{{type}}>>();
                    IsSupported = _codec is not null && _copier is not null
                        && (responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<{{type}}, {{codec}}>
                            || responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<{{type}}, global::Orleans.Serialization.Codecs.IFieldCodec<{{type}}>>)
                        && (responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<{{type}}, {{copier}}>
                            || responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<{{type}}, global::Orleans.Serialization.Cloning.IDeepCopier<{{type}}>>);
                }
                internal global::Orleans.Serialization.Invocation.Response RentCopied({{type}} value, global::Orleans.Serialization.Cloning.CopyContextPool contexts)
                {
                    {{rentCopiedBody}}
                }
                [return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull("input")]
                public {{name}} DeepCopy({{name}} input, global::Orleans.Serialization.Cloning.CopyContext context)
                    => input is null ? null : {{name}}.Rent({{(shallow ? "input.Value" : $"{copierReceiver}.DeepCopy(input.Value, context)")}}, this);
                internal void WriteResult<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, {{type}} value)
                    where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
                {
                    writer.WriteStartObject(0, null, typeof({{type}}));
                    {{writeBody}}
                    writer.WriteEndObject();
                }
                public override void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, object value)
                    => WriteResult(ref writer, (({{name}})value).Value);
                public override object ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
                    => ReadResult(ref reader, ref field);
                global::Orleans.Serialization.Invocation.Response global::Orleans.Serialization.Invocation.IRawResponseReader.ReadRaw<TInput>(
                    ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
                    => ReadResult(ref reader, ref field);
                private {{name}} ReadResult<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
                {
                    field.EnsureWireTypeTagDelimited();
                    var result = {{name}}.Rent(default, this);
                    try
                    {
                        reader.ReadFieldHeader(ref field);
                        if (!field.IsEndBaseOrEndObject)
                        {
                            result.Value = {{readResult}};
                            reader.ReadFieldHeader(ref field);
                            reader.ConsumeEndBaseOrEndObject(ref field);
                        }
                        return result;
                    }
                    catch
                    {
                        result.Dispose();
                        throw;
                    }
                }
                public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, {{name}} value)
                    where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
                {
                    if (value is null)
                    {
                        global::Orleans.Serialization.Codecs.ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                        return;
                    }
                    global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(writer.Session);
                    writer.WriteStartObject(fieldIdDelta, expectedType, typeof(global::Orleans.Serialization.Invocation.Response<{{type}}>));
                    {{writeEnvelopeBody}}
                    writer.WriteEndObject();
                }
                public {{name}} ReadValue<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
                {
                    if (field.IsReference)
                        return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<{{name}}, TInput>(ref reader, field);
                    global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
                    return ReadResult(ref reader, ref field);
                }
            }
            """;
    }

    private static bool ContainsParameter(ITypeSymbol type)
        => type is ITypeParameterSymbol or IErrorTypeSymbol
            || type is IArrayTypeSymbol array && ContainsParameter(array.ElementType)
            || type is INamedTypeSymbol named && (named.TypeArguments.Any(ContainsParameter)
                || named.ContainingType is { } containing && ContainsParameter(containing));
}
