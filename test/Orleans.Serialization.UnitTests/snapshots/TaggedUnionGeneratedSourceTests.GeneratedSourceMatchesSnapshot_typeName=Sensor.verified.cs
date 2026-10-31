#pragma warning disable
namespace OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Sensor : global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor>, global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor>
    {
        [global::System.Runtime.CompilerServices.UnsafeAccessorAttribute(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "<Location>k__BackingField")]
        private extern static ref string accessField_0(global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor instance);
        private readonly global::System.Type _codecFieldType = typeof(global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor);
        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Temperature _caseCodec0;
        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Humidity _caseCodec1;
        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Environmental _caseCodec2;
        private readonly OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Contact _caseCodec9;
        public Codec_Sensor(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _caseCodec0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Temperature>(this, codecProvider);
            _caseCodec1 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Humidity>(this, codecProvider);
            _caseCodec2 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Environmental>(this, codecProvider);
            _caseCodec9 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.Orleans.Serialization.UnitTests.TaggedUnions.Codec_Contact>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteEndBase();
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.Location);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor instance)
        {
            uint id = 0U;
            global::Orleans.Serialization.WireProtocol.Field header = default;
            {
                reader.ReadFieldHeader(ref header);
                reader.ConsumeEndBaseOrEndObject(ref header);
            }

            id = 0U;
            if (header.IsEndBaseFields)
                while (true)
                {
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                    if (id == 0U)
                    {
                        accessField_0(instance) = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                        reader.ReadFieldHeader(ref header);
                    }

                    reader.ConsumeEndBaseOrEndObject(ref header);
                    break;
                }
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            // A null value is an empty union field, which is also how an empty union value is written.
            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.WriteStartUnion(ref writer, fieldIdDelta, expectedType, _codecFieldType);
            switch (value)
            {
                case null:
                    break;
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
                default:
                    global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnsupportedUnionValue<global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor>(value);
                    break;
            }

            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor, TReaderInput>(ref reader, field);
            // Values written before the type used case identifiers encode their runtime type in the field header.
            if (field.FieldType is { } fieldType && fieldType != _codecFieldType)
                return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.DeserializeUnexpectedType<TReaderInput, global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor>(ref reader, ref field);
            field.EnsureWireTypeTagDelimited();
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            var header = global::Orleans.Serialization.Codecs.FieldHeaderCodec.ReadFieldHeader(ref reader);
            if (header.IsEndBaseOrEndObject)
                return null;
            global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor result;
            switch (header.FieldIdDelta)
            {
                case 0U:
                    result = _caseCodec0.ReadValue(ref reader, header);
                    break;
                case 1U:
                    result = _caseCodec1.ReadValue(ref reader, header);
                    break;
                case 2U:
                    result = _caseCodec2.ReadValue(ref reader, header);
                    break;
                case 9U:
                    result = _caseCodec9.ReadValue(ref reader, header);
                    break;
                default:
                    result = global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ThrowUnknownUnionCase<global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor>(header.FieldIdDelta);
                    break;
            }

            global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ConsumeEndBaseOrEndObject(ref reader);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Sensor : global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor>, global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor>
    {
        [global::System.Runtime.CompilerServices.UnsafeAccessorAttribute(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "<Location>k__BackingField")]
        private extern static ref string accessField_0(global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor instance);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor DeepCopy(global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            return context.DeepCopy(original);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor input, global::Orleans.Serialization.UnitTests.TaggedUnions.Sensor output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            accessField_0(output) = input.Location;
        }
    }
}
#pragma warning restore