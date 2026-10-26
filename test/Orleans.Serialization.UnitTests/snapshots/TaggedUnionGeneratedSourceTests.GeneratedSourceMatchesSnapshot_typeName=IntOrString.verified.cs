#pragma warning disable
namespace OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_IntOrString : global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString>, global::Orleans.Serialization.Serializers.IValueSerializer<global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString);
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            switch (instance)
            {
                case int case0:
                    global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, case0);
                    break;
                case string case1:
                    global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, case1);
                    break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString instance)
        {
            var header = global::Orleans.Serialization.Codecs.FieldHeaderCodec.ReadFieldHeader(ref reader);
            if (header.IsEndBaseOrEndObject)
            {
                instance = default;
                return;
            }

            switch (header.FieldIdDelta)
            {
                case 0U:
                    instance = (int)global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    break;
                case 1U:
                    instance = (string)global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                    break;
                default:
                    instance = global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnknownUnionCase<global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString>(header.FieldIdDelta);
                    break;
            }

            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ConsumeEndBaseOrEndObject(ref reader);
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.WriteStartUnion(ref writer, fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, ref value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            // Null references and values which encode their runtime type are read as case values, so payloads written for a closed type
            // hierarchy with the same cases, including payloads written before that hierarchy used case identifiers, are readable.
            if (field.IsReference)
                return ReadCaseValue(ref reader, ref field);
            if (field.FieldType is { } fieldType && fieldType != _codecFieldType)
                return ReadCaseValue(ref reader, ref field);
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.BeginUnionRead(ref reader, field);
            global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString result = default;
            Deserialize(ref reader, ref result);
            return result;
        }

        private global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString ReadCaseValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString instance = default;
            var value = field.IsReference ? global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<object, TReaderInput>(ref reader, field) : global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TReaderInput, object>(ref reader, ref field);
            switch (value)
            {
                case null:
                    instance = default;
                    break;
                case int case0:
                    instance = (int)case0;
                    break;
                case string case1:
                    instance = (string)case1;
                    break;
                default:
                    global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnsupportedUnionValue<global::Orleans.Serialization.UnitTests.TaggedUnions.IntOrString>(value);
                    break;
            }

            return instance;
        }
    }
}
#pragma warning restore