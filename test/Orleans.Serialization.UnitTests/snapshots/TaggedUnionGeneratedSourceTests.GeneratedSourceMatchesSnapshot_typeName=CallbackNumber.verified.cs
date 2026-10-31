#pragma warning disable
namespace OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_CallbackNumber : global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber>, global::Orleans.Serialization.Serializers.IValueSerializer<global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber);
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber instance)
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
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber instance)
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
                    instance = global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnknownUnionCase<global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber>(header.FieldIdDelta);
                    break;
            }

            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ConsumeEndBaseOrEndObject(ref reader);
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.WriteStartUnion(ref writer, fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, ref value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            // Null references and values which encode their runtime type are read as case values, so payloads written for a closed type
            // hierarchy with the same cases, including payloads written before that hierarchy used case identifiers, are readable.
            if (field.IsReference)
                return ReadCaseValue(ref reader, ref field);
            if (field.FieldType is { } fieldType && fieldType != _codecFieldType)
                return ReadCaseValue(ref reader, ref field);
            field.EnsureWireTypeTagDelimited();
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber result = default;
            Deserialize(ref reader, ref result);
            return result;
        }

        private global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber ReadCaseValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber instance = default;
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
                    global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnsupportedUnionValue<global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber>(value);
                    break;
            }

            return instance;
        }

        private readonly global::Orleans.Serialization.UnitTests.TaggedUnions.NumberCallbacks _hook0;
        public Codec_CallbackNumber(global::Orleans.Serialization.UnitTests.TaggedUnions.NumberCallbacks _hook0)
        {
            this._hook0 = OrleansGeneratedCodeHelper.UnwrapService(this, _hook0);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_CallbackNumber : global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber>
    {
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber DeepCopy(global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.CallbackNumber result = original;
            _hook0.OnCopied(original, ref result);
            return result;
        }

        private readonly global::Orleans.Serialization.UnitTests.TaggedUnions.NumberCallbacks _hook0;
        public Copier_CallbackNumber(global::Orleans.Serialization.UnitTests.TaggedUnions.NumberCallbacks _hook0)
        {
            this._hook0 = OrleansGeneratedCodeHelper.UnwrapService(this, _hook0);
        }
    }
}
#pragma warning restore