#pragma warning disable
namespace OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Either<L, R> : global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R>>, global::Orleans.Serialization.Serializers.IValueSerializer<global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R>>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R>);
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            switch (instance)
            {
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L> case0:
                    _caseCodec0.WriteField(ref writer, 0U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>), case0);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R> case1:
                    _caseCodec1.WriteField(ref writer, 1U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>), case1);
                    break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> instance)
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
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>)_caseCodec0.ReadValue(ref reader, header);
                    break;
                case 1U:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>)_caseCodec1.ReadValue(ref reader, header);
                    break;
                default:
                    instance = global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnknownUnionCase<global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R>>(header.FieldIdDelta);
                    break;
            }

            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ConsumeEndBaseOrEndObject(ref reader);
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.WriteStartUnion(ref writer, fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, ref value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            // Null references and values which encode their runtime type are read as case values, so payloads written for a closed type
            // hierarchy with the same cases, including payloads written before that hierarchy used case identifiers, are readable.
            if (field.IsReference)
                return ReadCaseValue(ref reader, ref field);
            if (field.FieldType is { } fieldType && fieldType != _codecFieldType)
                return ReadCaseValue(ref reader, ref field);
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.BeginUnionRead(ref reader, field);
            global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> result = default;
            Deserialize(ref reader, ref result);
            return result;
        }

        private global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> ReadCaseValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> instance = default;
            var value = field.IsReference ? global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<object, TReaderInput>(ref reader, field) : global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TReaderInput, object>(ref reader, ref field);
            switch (value)
            {
                case null:
                    instance = default;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L> case0:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>)case0;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R> case1:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>)case1;
                    break;
                default:
                    global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnsupportedUnionValue<global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R>>(value);
                    break;
            }

            return instance;
        }

        private readonly global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>> _caseCodec0;
        private readonly global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>> _caseCodec1;
        public Codec_Either(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _caseCodec0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>>>(this, codecProvider);
            _caseCodec1 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>>>(this, codecProvider);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Either<L, R> : global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R>>
    {
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> DeepCopy(global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.Either<L, R> result = default;
            switch (original)
            {
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L> case0:
                    result = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>)_caseCopier0.DeepCopy(case0, context);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R> case1:
                    result = (global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>)_caseCopier1.DeepCopy(case1, context);
                    break;
                default:
                    result = original;
                    break;
            }

            return result;
        }

        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>> _caseCopier0;
        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>> _caseCopier1;
        public Copier_Either(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _caseCopier0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<L>>>(this, codecProvider);
            _caseCopier1 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Value<R>>>(this, codecProvider);
        }
    }
}
#pragma warning restore