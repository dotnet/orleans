#pragma warning disable
namespace OrleansCodeGen
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Base : global::Orleans.Serialization.Codecs.IFieldCodec<global::Base>, global::Orleans.Serialization.Serializers.IBaseCodec<global::Base>
    {
        private readonly global::Orleans.Serialization.Serializers.ICodecProvider _codecProvider;
        private readonly global::System.Type _codecFieldType = typeof(global::Base);
        private OrleansCodeGen.Codec_Added _caseCodec1;
        private OrleansCodeGen.Codec_Existing _caseCodec9;
        public Codec_Base(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _codecProvider = codecProvider;
            _caseCodec1 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Codec_Added>(this, codecProvider);
            _caseCodec9 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Codec_Existing>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::Base instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Base instance)
        {
            global::Orleans.Serialization.WireProtocol.Field header = default;
            {
                reader.ReadFieldHeader(ref header);
                reader.ConsumeEndBaseOrEndObject(ref header);
            }

            if (header.IsEndBaseFields)
            {
                reader.ReadFieldHeader(ref header);
                reader.ConsumeEndBaseOrEndObject(ref header);
            }
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Base value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            // A null value is an empty union field, which is also how an empty union value is written.
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.WriteStartUnion(ref writer, fieldIdDelta, expectedType, _codecFieldType);
            switch (value)
            {
                case null:
                    break;
                case global::Added case0:
                    (_caseCodec1 ??= OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Codec_Added>(this, _codecProvider)).WriteField(ref writer, 1U, typeof(global::Added), case0);
                    break;
                case global::Existing case1:
                    (_caseCodec9 ??= OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Codec_Existing>(this, _codecProvider)).WriteField(ref writer, 9U, typeof(global::Existing), case1);
                    break;
                default:
                    global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnsupportedUnionValue<global::Base>(value);
                    break;
            }

            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Base ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<global::Base, TReaderInput>(ref reader, field);
            // Values written before the type used case identifiers encode their runtime type in the field header.
            if (field.FieldType is { } fieldType && fieldType != _codecFieldType)
                return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TReaderInput, global::Base>(ref reader, ref field);
            field.EnsureWireTypeTagDelimited();
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            var header = global::Orleans.Serialization.Codecs.FieldHeaderCodec.ReadFieldHeader(ref reader);
            if (header.IsEndBaseOrEndObject)
                return null;
            global::Base result;
            switch (header.FieldIdDelta)
            {
                case 1U:
                    result = (_caseCodec1 ??= OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Codec_Added>(this, _codecProvider)).ReadValue(ref reader, header);
                    break;
                case 9U:
                    result = (_caseCodec9 ??= OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Codec_Existing>(this, _codecProvider)).ReadValue(ref reader, header);
                    break;
                default:
                    result = global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnknownUnionCase<global::Base>(header.FieldIdDelta);
                    break;
            }

            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ConsumeEndBaseOrEndObject(ref reader);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Base : global::Orleans.Serialization.Cloning.IDeepCopier<global::Base>, global::Orleans.Serialization.Cloning.IBaseCopier<global::Base>
    {
        private readonly global::Orleans.Serialization.Serializers.ICodecProvider _codecProvider;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Base DeepCopy(global::Base original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            return context.DeepCopy(original);
        }

        public Copier_Base(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _codecProvider = codecProvider;
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::Base input, global::Base output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
        }
    }
}
#pragma warning restore