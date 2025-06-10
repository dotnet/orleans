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
    public sealed class Codec_OptionalCtorParams : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.OptionalCtorParams>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.OptionalCtorParams>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.OptionalCtorParams);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.OptionalCtorParams> _activator;
        private static readonly global::System.Func<global::TestProject.OptionalCtorParams, int> getField0 = (global::System.Func<global::TestProject.OptionalCtorParams, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.OptionalCtorParams), "_x");
        private static readonly global::System.Action<global::TestProject.OptionalCtorParams, int> setField0 = (global::System.Action<global::TestProject.OptionalCtorParams, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.OptionalCtorParams), "_x");
        private static readonly global::System.Func<global::TestProject.OptionalCtorParams, string> getField1 = (global::System.Func<global::TestProject.OptionalCtorParams, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.OptionalCtorParams), "_y");
        private static readonly global::System.Action<global::TestProject.OptionalCtorParams, string> setField1 = (global::System.Action<global::TestProject.OptionalCtorParams, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.OptionalCtorParams), "_y");
        public Codec_OptionalCtorParams(global::Orleans.Serialization.Activators.IActivator<global::TestProject.OptionalCtorParams> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.OptionalCtorParams instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, getField0(instance));
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, getField1(instance));
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.OptionalCtorParams instance)
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
                    setField0(instance, global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    setField1(instance, global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.OptionalCtorParams @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.OptionalCtorParams))
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
        public global::TestProject.OptionalCtorParams ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.OptionalCtorParams, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.OptionalCtorParams>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_OptionalCtorParams : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.OptionalCtorParams>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.OptionalCtorParams>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.OptionalCtorParams> _activator;
        private static readonly global::System.Func<global::TestProject.OptionalCtorParams, int> getField0 = (global::System.Func<global::TestProject.OptionalCtorParams, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.OptionalCtorParams), "_x");
        private static readonly global::System.Action<global::TestProject.OptionalCtorParams, int> setField0 = (global::System.Action<global::TestProject.OptionalCtorParams, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.OptionalCtorParams), "_x");
        private static readonly global::System.Func<global::TestProject.OptionalCtorParams, string> getField1 = (global::System.Func<global::TestProject.OptionalCtorParams, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.OptionalCtorParams), "_y");
        private static readonly global::System.Action<global::TestProject.OptionalCtorParams, string> setField1 = (global::System.Action<global::TestProject.OptionalCtorParams, string>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.OptionalCtorParams), "_y");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.OptionalCtorParams DeepCopy(global::TestProject.OptionalCtorParams original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.OptionalCtorParams existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.OptionalCtorParams))
                return context.DeepCopy(original);
            var result = _activator.Create();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_OptionalCtorParams(global::Orleans.Serialization.Activators.IActivator<global::TestProject.OptionalCtorParams> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.OptionalCtorParams input, global::TestProject.OptionalCtorParams output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField0(output, getField0(input));
            setField1(output, getField1(input));
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_OptionalCtorParams));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_OptionalCtorParams));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
