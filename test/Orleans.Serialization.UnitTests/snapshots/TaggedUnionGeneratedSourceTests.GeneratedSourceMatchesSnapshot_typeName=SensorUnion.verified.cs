#pragma warning disable
namespace OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_SensorUnion : global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion>, global::Orleans.Serialization.Serializers.IValueSerializer<global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion);
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            switch (instance)
            {
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature case0:
                    _caseCodec0.WriteField(ref writer, 0U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature), case0);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity case1:
                    _caseCodec1.WriteField(ref writer, 1U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity), case1);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental case2:
                    _caseCodec2.WriteField(ref writer, 2U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental), case2);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Contact case3:
                    _caseCodec9.WriteField(ref writer, 9U, typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Contact), case3);
                    break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion instance)
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
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature)_caseCodec0.ReadValue(ref reader, header);
                    break;
                case 1U:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity)_caseCodec1.ReadValue(ref reader, header);
                    break;
                case 2U:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental)_caseCodec2.ReadValue(ref reader, header);
                    break;
                case 9U:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Contact)_caseCodec9.ReadValue(ref reader, header);
                    break;
                default:
                    instance = global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnknownUnionCase<global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion>(header.FieldIdDelta);
                    break;
            }

            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ConsumeEndBaseOrEndObject(ref reader);
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.WriteStartUnion(ref writer, fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, ref value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            // Null references and values which encode their runtime type are read as case values, so payloads written for a closed type
            // hierarchy with the same cases, including payloads written before that hierarchy used case identifiers, are readable.
            if (field.IsReference)
                return ReadCaseValue(ref reader, ref field);
            if (field.FieldType is { } fieldType && fieldType != _codecFieldType)
                return ReadCaseValue(ref reader, ref field);
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.BeginUnionRead(ref reader, field);
            global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion result = default;
            Deserialize(ref reader, ref result);
            return result;
        }

        private global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion ReadCaseValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion instance = default;
            var value = field.IsReference ? global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<object, TReaderInput>(ref reader, field) : global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TReaderInput, object>(ref reader, ref field);
            switch (value)
            {
                case null:
                    instance = default;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature case0:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature)case0;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity case1:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity)case1;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental case2:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental)case2;
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Contact case3:
                    instance = (global::Orleans.Serialization.UnitTests.TaggedUnions.Contact)case3;
                    break;
                default:
                    global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnsupportedUnionValue<global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion>(value);
                    break;
            }

            return instance;
        }

        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Temperature _caseCodec0;
        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Humidity _caseCodec1;
        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Environmental _caseCodec2;
        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Contact _caseCodec9;
        public Codec_SensorUnion(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _caseCodec0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Temperature>(this, codecProvider);
            _caseCodec1 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Humidity>(this, codecProvider);
            _caseCodec2 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Environmental>(this, codecProvider);
            _caseCodec9 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Contact>(this, codecProvider);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_SensorUnion : global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion>
    {
        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion DeepCopy(global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            global::Orleans.Serialization.UnitTests.TaggedUnions.SensorUnion result = default;
            switch (original)
            {
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature case0:
                    result = (global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature)_caseCopier0.DeepCopy(case0, context);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity case1:
                    result = (global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity)_caseCopier1.DeepCopy(case1, context);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental case2:
                    result = (global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental)_caseCopier2.DeepCopy(case2, context);
                    break;
                case global::Orleans.Serialization.UnitTests.TaggedUnions.Contact case3:
                    result = (global::Orleans.Serialization.UnitTests.TaggedUnions.Contact)_caseCopier9.DeepCopy(case3, context);
                    break;
                default:
                    result = original;
                    break;
            }

            return result;
        }

        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature> _caseCopier0;
        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity> _caseCopier1;
        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental> _caseCopier2;
        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Contact> _caseCopier9;
        public Copier_SensorUnion(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _caseCopier0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Temperature>>(this, codecProvider);
            _caseCopier1 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Humidity>>(this, codecProvider);
            _caseCopier2 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Environmental>>(this, codecProvider);
            _caseCopier9 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Contact>>(this, codecProvider);
        }
    }
}
#pragma warning restore