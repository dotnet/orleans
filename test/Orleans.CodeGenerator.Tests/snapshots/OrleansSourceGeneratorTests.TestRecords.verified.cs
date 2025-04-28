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
    public sealed class Codec_DemoDataRecordStruct : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.DemoDataRecordStruct>, global::Orleans.Serialization.Serializers.IValueSerializer<global::TestProject.DemoDataRecordStruct>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.DemoDataRecordStruct);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, scoped ref global::TestProject.DemoDataRecordStruct instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteEndBase();
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.Value);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, scoped ref global::TestProject.DemoDataRecordStruct instance)
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
                        instance.Value = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                        reader.ReadFieldHeader(ref header);
                    }

                    reader.ConsumeEndBaseOrEndObject(ref header);
                    break;
                }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.DemoDataRecordStruct @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, ref @value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.DemoDataRecordStruct ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = default(global::TestProject.DemoDataRecordStruct);
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, ref result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_DemoDataRecordClass : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.DemoDataRecordClass>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.DemoDataRecordClass>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.DemoDataRecordClass);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecordClass> _activator;
        private static readonly global::System.Action<global::TestProject.DemoDataRecordClass, string> setField0 = (global::System.Action<global::TestProject.DemoDataRecordClass, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.DemoDataRecordClass), "<Value>k__BackingField");
        public Codec_DemoDataRecordClass(global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecordClass> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.DemoDataRecordClass instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteEndBase();
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.Value);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.DemoDataRecordClass instance)
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
                        setField0(instance, global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header));
                        reader.ReadFieldHeader(ref header);
                    }

                    reader.ConsumeEndBaseOrEndObject(ref header);
                    break;
                }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.DemoDataRecordClass @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.DemoDataRecordClass))
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
        public global::TestProject.DemoDataRecordClass ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.DemoDataRecordClass, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.DemoDataRecordClass>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_DemoDataRecordClass : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.DemoDataRecordClass>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.DemoDataRecordClass>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecordClass> _activator;
        private static readonly global::System.Action<global::TestProject.DemoDataRecordClass, string> setField0 = (global::System.Action<global::TestProject.DemoDataRecordClass, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.DemoDataRecordClass), "<Value>k__BackingField");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.DemoDataRecordClass DeepCopy(global::TestProject.DemoDataRecordClass original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.DemoDataRecordClass existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.DemoDataRecordClass))
                return context.DeepCopy(original);
            var result = _activator.Create();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_DemoDataRecordClass(global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecordClass> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.DemoDataRecordClass input, global::TestProject.DemoDataRecordClass output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField0(output, input.Value);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_DemoDataRecord : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.DemoDataRecord>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.DemoDataRecord>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.DemoDataRecord);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecord> _activator;
        private static readonly global::System.Action<global::TestProject.DemoDataRecord, string> setField0 = (global::System.Action<global::TestProject.DemoDataRecord, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.DemoDataRecord), "<Value>k__BackingField");
        public Codec_DemoDataRecord(global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecord> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.DemoDataRecord instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteEndBase();
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.Value);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.DemoDataRecord instance)
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
                        setField0(instance, global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header));
                        reader.ReadFieldHeader(ref header);
                    }

                    reader.ConsumeEndBaseOrEndObject(ref header);
                    break;
                }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.DemoDataRecord @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.DemoDataRecord))
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
        public global::TestProject.DemoDataRecord ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.DemoDataRecord, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.DemoDataRecord>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_DemoDataRecord : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.DemoDataRecord>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.DemoDataRecord>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecord> _activator;
        private static readonly global::System.Action<global::TestProject.DemoDataRecord, string> setField0 = (global::System.Action<global::TestProject.DemoDataRecord, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.DemoDataRecord), "<Value>k__BackingField");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.DemoDataRecord DeepCopy(global::TestProject.DemoDataRecord original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.DemoDataRecord existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.DemoDataRecord))
                return context.DeepCopy(original);
            var result = _activator.Create();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_DemoDataRecord(global::Orleans.Serialization.Activators.IActivator<global::TestProject.DemoDataRecord> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.DemoDataRecord input, global::TestProject.DemoDataRecord output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField0(output, input.Value);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_DemoDataRecordStruct));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_DemoDataRecordClass));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_DemoDataRecord));
            config.Copiers.Add(typeof(global::Orleans.Serialization.Cloning.ShallowCopier<global::TestProject.DemoDataRecordStruct>));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_DemoDataRecordClass));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_DemoDataRecord));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
