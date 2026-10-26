#pragma warning disable
namespace OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_ImmutableCallbackPet : global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet>, global::Orleans.Serialization.Serializers.IValueSerializer<global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet);
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            switch (instance)
            {
                case global::Orleans.Serialization.UnitTests.TaggedUnions.OpaqueCase case0:
                    _caseCodec0.WriteField(ref writer, 0U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.OpaqueCase), case0);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<global::System.Collections.Generic.List<int>> case1:
                    _caseCodec1.WriteField(ref writer, 1U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Value<global::System.Collections.Generic.List<int>>), case1);
                    break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet instance)
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
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.OpaqueCase)_caseCodec0.ReadValue(ref reader, header);
                    break;
                case 1U:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<global::System.Collections.Generic.List<int>>)_caseCodec1.ReadValue(ref reader, header);
                    break;
                default:
                    instance = global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnknownUnionCase<global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet>(header.FieldIdDelta);
                    break;
            }

            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ConsumeEndBaseOrEndObject(ref reader);
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.WriteStartUnion(ref writer, fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, ref value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            // Null references and values which encode their runtime type are read as case values, so payloads written for a closed type
            // hierarchy with the same cases, including payloads written before that hierarchy used case identifiers, are readable.
            if (field.IsReference)
                return ReadCaseValue(ref reader, ref field);
            if (field.FieldType is { } fieldType && fieldType != _codecFieldType)
                return ReadCaseValue(ref reader, ref field);
            field.EnsureWireTypeTagDelimited();
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet result = default;
            Deserialize(ref reader, ref result);
            return result;
        }

        private global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet ReadCaseValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet instance = default;
            var value = field.IsReference ? global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<object, TReaderInput>(ref reader, field) : global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TReaderInput, object>(ref reader, ref field);
            switch (value)
            {
                case null:
                    instance = default;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.OpaqueCase case0:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.OpaqueCase)case0;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<global::System.Collections.Generic.List<int>> case1:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<global::System.Collections.Generic.List<int>>)case1;
                    break;
                default:
                    global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnsupportedUnionValue<global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet>(value);
                    break;
            }

            return instance;
        }

        private readonly global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.OpaqueCase> _caseCodec0;
        private readonly global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<global::System.Collections.Generic.List<int>>> _caseCodec1;
        private readonly global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutablePetCallbacks _hook0;
        public Codec_ImmutableCallbackPet(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider, global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutablePetCallbacks _hook0)
        {
            _caseCodec0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.OpaqueCase>>(this, codecProvider);
            _caseCodec1 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<global::System.Collections.Generic.List<int>>>>(this, codecProvider);
            this._hook0 = OrleansGeneratedCodeHelper.UnwrapService(this, _hook0);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_ImmutableCallbackPet : global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet>
    {
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet DeepCopy(global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutableCallbackPet result = default;
            _hook0.OnCopying(original, ref result);
            switch (original)
            {
                default:
                    result = original;
                    break;
            }

            _hook0.OnCopied(original, ref result);
            return result;
        }

        private readonly global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutablePetCallbacks _hook0;
        public Copier_ImmutableCallbackPet(global::Orleans.Serialization.UnitTests.TaggedUnions.ImmutablePetCallbacks _hook0)
        {
            this._hook0 = OrleansGeneratedCodeHelper.UnwrapService(this, _hook0);
        }
    }
}
#pragma warning restore