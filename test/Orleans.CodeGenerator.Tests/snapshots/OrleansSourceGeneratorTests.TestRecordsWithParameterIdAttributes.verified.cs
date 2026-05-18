#pragma warning disable CS1591, RS0016, RS0041
[assembly: global::Orleans.ApplicationPartAttribute("TestProject")]
[assembly: global::Orleans.ApplicationPartAttribute("Orleans.Core.Abstractions")]
[assembly: global::Orleans.ApplicationPartAttribute("Orleans.Serialization")]
[assembly: global::Orleans.ApplicationPartAttribute("Orleans.Core")]
[assembly: global::Orleans.ApplicationPartAttribute("Orleans.Runtime")]
[assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof(OrleansCodeGen.TestProject.Metadata_TestProject))]
namespace OrleansCodeGen.TestProject
{
    using global::Orleans.Serialization.Codecs;
    using global::Orleans.Serialization.GeneratedCodeHelpers;

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_SimpleRecord : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.SimpleRecord>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.SimpleRecord>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.SimpleRecord);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.SimpleRecord> _activator;
        private static readonly global::System.Action<global::TestProject.SimpleRecord, string> setField0 = (global::System.Action<global::TestProject.SimpleRecord, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.SimpleRecord), "<Name>k__BackingField");
        private static readonly global::System.Action<global::TestProject.SimpleRecord, int> setField1 = (global::System.Action<global::TestProject.SimpleRecord, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.SimpleRecord), "<Value>k__BackingField");
        public Codec_SimpleRecord(global::Orleans.Serialization.Activators.IActivator<global::TestProject.SimpleRecord> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.SimpleRecord instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.Value);
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, instance.Name);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.SimpleRecord instance)
        {
            uint id = 0U;
            global::Orleans.Serialization.WireProtocol.Field header = default;
            while (true)
            {
                reader.ReadFieldHeader(ref header);
                if (header.IsEndBaseOrEndObject)
                    break;
                id += header.FieldIdDelta;
                if (id == 0U)
                {
                    setField1(instance, global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    setField0(instance, global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }

            id = 0U;
            if (header.IsEndBaseFields)
            {
                reader.ReadFieldHeader(ref header);
                reader.ConsumeEndBaseOrEndObject(ref header);
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.SimpleRecord @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.SimpleRecord))
            {
                if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, @value))
                    return;
                writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
                Serialize(ref writer, @value);
                writer.WriteEndObject();
            }
            else
                writer.SerializeUnexpectedType(fieldIdDelta, expectedType, @value);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.SimpleRecord ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.SimpleRecord, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.SimpleRecord>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_SimpleRecord : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.SimpleRecord>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.SimpleRecord>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.SimpleRecord> _activator;
        private static readonly global::System.Action<global::TestProject.SimpleRecord, string> setField0 = (global::System.Action<global::TestProject.SimpleRecord, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.SimpleRecord), "<Name>k__BackingField");
        private static readonly global::System.Action<global::TestProject.SimpleRecord, int> setField1 = (global::System.Action<global::TestProject.SimpleRecord, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.SimpleRecord), "<Value>k__BackingField");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.SimpleRecord DeepCopy(global::TestProject.SimpleRecord original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.SimpleRecord existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.SimpleRecord))
                return context.DeepCopy(original);
            var result = _activator.Create();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_SimpleRecord(global::Orleans.Serialization.Activators.IActivator<global::TestProject.SimpleRecord> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.SimpleRecord input, global::TestProject.SimpleRecord output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField1(output, input.Value);
            setField0(output, input.Name);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_RecordWithExtraProperty : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.RecordWithExtraProperty>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.RecordWithExtraProperty>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.RecordWithExtraProperty);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.RecordWithExtraProperty> _activator;
        private static readonly global::System.Action<global::TestProject.RecordWithExtraProperty, string> setField0 = (global::System.Action<global::TestProject.RecordWithExtraProperty, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.RecordWithExtraProperty), "<Description>k__BackingField");
        private static readonly global::System.Action<global::TestProject.RecordWithExtraProperty, int> setField1 = (global::System.Action<global::TestProject.RecordWithExtraProperty, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.RecordWithExtraProperty), "<Id>k__BackingField");
        private static readonly global::System.Action<global::TestProject.RecordWithExtraProperty, string> setField2 = (global::System.Action<global::TestProject.RecordWithExtraProperty, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.RecordWithExtraProperty), "<Name>k__BackingField");
        public Codec_RecordWithExtraProperty(global::Orleans.Serialization.Activators.IActivator<global::TestProject.RecordWithExtraProperty> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.RecordWithExtraProperty instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.Id);
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, instance.Name);
            writer.WriteEndBase();
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 2U, instance.Description);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.RecordWithExtraProperty instance)
        {
            uint id = 0U;
            global::Orleans.Serialization.WireProtocol.Field header = default;
            while (true)
            {
                reader.ReadFieldHeader(ref header);
                if (header.IsEndBaseOrEndObject)
                    break;
                id += header.FieldIdDelta;
                if (id == 0U)
                {
                    setField1(instance, global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    setField2(instance, global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }

            id = 0U;
            if (header.IsEndBaseFields)
                while (true)
                {
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                    if (id == 2U)
                    {
                        setField0(instance, global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header));
                        reader.ReadFieldHeader(ref header);
                        if (header.IsEndBaseOrEndObject)
                            break;
                        id++;
                    }

                    reader.ConsumeUnknownField(ref header);
                }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.RecordWithExtraProperty @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.RecordWithExtraProperty))
            {
                if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, @value))
                    return;
                writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
                Serialize(ref writer, @value);
                writer.WriteEndObject();
            }
            else
                writer.SerializeUnexpectedType(fieldIdDelta, expectedType, @value);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.RecordWithExtraProperty ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.RecordWithExtraProperty, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.RecordWithExtraProperty>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_RecordWithExtraProperty : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.RecordWithExtraProperty>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.RecordWithExtraProperty>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.RecordWithExtraProperty> _activator;
        private static readonly global::System.Action<global::TestProject.RecordWithExtraProperty, string> setField0 = (global::System.Action<global::TestProject.RecordWithExtraProperty, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.RecordWithExtraProperty), "<Description>k__BackingField");
        private static readonly global::System.Action<global::TestProject.RecordWithExtraProperty, int> setField1 = (global::System.Action<global::TestProject.RecordWithExtraProperty, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.RecordWithExtraProperty), "<Id>k__BackingField");
        private static readonly global::System.Action<global::TestProject.RecordWithExtraProperty, string> setField2 = (global::System.Action<global::TestProject.RecordWithExtraProperty, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.RecordWithExtraProperty), "<Name>k__BackingField");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.RecordWithExtraProperty DeepCopy(global::TestProject.RecordWithExtraProperty original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.RecordWithExtraProperty existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.RecordWithExtraProperty))
                return context.DeepCopy(original);
            var result = _activator.Create();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_RecordWithExtraProperty(global::Orleans.Serialization.Activators.IActivator<global::TestProject.RecordWithExtraProperty> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.RecordWithExtraProperty input, global::TestProject.RecordWithExtraProperty output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField1(output, input.Id);
            setField2(output, input.Name);
            setField0(output, input.Description);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_RecordStructWithParameterId : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.RecordStructWithParameterId>, global::Orleans.Serialization.Serializers.IValueSerializer<global::TestProject.RecordStructWithParameterId>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.RecordStructWithParameterId);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::TestProject.RecordStructWithParameterId instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.Value);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::TestProject.RecordStructWithParameterId instance)
        {
            uint id = 0U;
            global::Orleans.Serialization.WireProtocol.Field header = default;
            while (true)
            {
                reader.ReadFieldHeader(ref header);
                if (header.IsEndBaseOrEndObject)
                    break;
                id += header.FieldIdDelta;
                if (id == 0U)
                {
                    instance.Value = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }

            id = 0U;
            if (header.IsEndBaseFields)
            {
                reader.ReadFieldHeader(ref header);
                reader.ConsumeEndBaseOrEndObject(ref header);
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.RecordStructWithParameterId @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, ref @value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.RecordStructWithParameterId ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = default(global::TestProject.RecordStructWithParameterId);
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, ref result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_SimpleRecord));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_RecordWithExtraProperty));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_RecordStructWithParameterId));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_SimpleRecord));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_RecordWithExtraProperty));
            config.Copiers.Add(typeof(global::Orleans.Serialization.Cloning.ShallowCopier<global::TestProject.RecordStructWithParameterId>));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
