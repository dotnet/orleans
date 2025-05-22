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
    public sealed class Codec_MyCustomEnum : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.MyCustomEnum>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.MyCustomEnum);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.MyCustomEnum @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, fieldIdDelta, expectedType, (int)@value, _codecFieldType);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.MyCustomEnum ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            return (global::TestProject.MyCustomEnum)global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_ClassWithImplicitFieldIds : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.ClassWithImplicitFieldIds>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.ClassWithImplicitFieldIds>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.ClassWithImplicitFieldIds);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.ClassWithImplicitFieldIds> _activator;
        private readonly global::System.Type _type0 = typeof(global::TestProject.MyCustomEnum);
        private readonly OrleansCodeGen.TestProject.Codec_MyCustomEnum _codec0;
        private static readonly global::System.Action<global::TestProject.ClassWithImplicitFieldIds, global::TestProject.MyCustomEnum> setField0 = (global::System.Action<global::TestProject.ClassWithImplicitFieldIds, global::TestProject.MyCustomEnum>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.ClassWithImplicitFieldIds), "<EnumValue>k__BackingField");
        private static readonly global::System.Action<global::TestProject.ClassWithImplicitFieldIds, string> setField1 = (global::System.Action<global::TestProject.ClassWithImplicitFieldIds, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.ClassWithImplicitFieldIds), "<StringValue>k__BackingField");
        public Codec_ClassWithImplicitFieldIds(global::Orleans.Serialization.Activators.IActivator<global::TestProject.ClassWithImplicitFieldIds> _activator, global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
            _codec0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Codec_MyCustomEnum>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.ClassWithImplicitFieldIds instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _codec0.WriteField(ref writer, 19816600U, _type0, instance.EnumValue);
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1774218397U, instance.StringValue);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.ClassWithImplicitFieldIds instance)
        {
            uint id = 0U;
            global::Orleans.Serialization.WireProtocol.Field header = default;
            while (true)
            {
                reader.ReadFieldHeader(ref header);
                if (header.IsEndBaseOrEndObject)
                    break;
                id += header.FieldIdDelta;
                if (id == 19816600U)
                {
                    setField0(instance, _codec0.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1794034997U)
                {
                    setField1(instance, global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id++;
                }

                reader.ConsumeUnknownField(ref header);
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.ClassWithImplicitFieldIds @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.ClassWithImplicitFieldIds))
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
        public global::TestProject.ClassWithImplicitFieldIds ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.ClassWithImplicitFieldIds, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.ClassWithImplicitFieldIds>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_ClassWithImplicitFieldIds : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.ClassWithImplicitFieldIds>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.ClassWithImplicitFieldIds>
    {
        private static readonly global::System.Action<global::TestProject.ClassWithImplicitFieldIds, global::TestProject.MyCustomEnum> setField0 = (global::System.Action<global::TestProject.ClassWithImplicitFieldIds, global::TestProject.MyCustomEnum>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.ClassWithImplicitFieldIds), "<EnumValue>k__BackingField");
        private static readonly global::System.Action<global::TestProject.ClassWithImplicitFieldIds, string> setField1 = (global::System.Action<global::TestProject.ClassWithImplicitFieldIds, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.ClassWithImplicitFieldIds), "<StringValue>k__BackingField");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.ClassWithImplicitFieldIds DeepCopy(global::TestProject.ClassWithImplicitFieldIds original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            return original is null || original.GetType() == typeof(global::TestProject.ClassWithImplicitFieldIds) ? original : context.DeepCopy(original);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.ClassWithImplicitFieldIds input, global::TestProject.ClassWithImplicitFieldIds output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField0(output, input.EnumValue);
            setField1(output, input.StringValue);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_MyCustomEnum));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_ClassWithImplicitFieldIds));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_ClassWithImplicitFieldIds));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
