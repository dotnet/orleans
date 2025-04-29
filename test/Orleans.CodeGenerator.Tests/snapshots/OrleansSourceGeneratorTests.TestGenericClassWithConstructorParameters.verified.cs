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
    public sealed class Codec_GenericDemoDataWithCtorParams<T> : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.GenericDemoDataWithCtorParams<T>>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.GenericDemoDataWithCtorParams<T>>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.GenericDemoDataWithCtorParams<T>);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericDemoDataWithCtorParams<T>> _activator;
        private readonly global::System.Type _type0 = typeof(T);
        private readonly global::Orleans.Serialization.Codecs.IFieldCodec<T> _codec0;
        private static readonly global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, int> setField0 = (global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericDemoDataWithCtorParams<T>), "<IntValue>k__BackingField");
        private static readonly global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, T> setField1 = (global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, T>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericDemoDataWithCtorParams<T>), "<Value>k__BackingField");
        public Codec_GenericDemoDataWithCtorParams(global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericDemoDataWithCtorParams<T>> _activator, global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
            _codec0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.IFieldCodec<T>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.GenericDemoDataWithCtorParams<T> instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _codec0.WriteField(ref writer, 0U, _type0, instance.Value);
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.IntValue);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.GenericDemoDataWithCtorParams<T> instance)
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
                    setField1(instance, _codec0.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    setField0(instance, global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header));
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.GenericDemoDataWithCtorParams<T> @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.GenericDemoDataWithCtorParams<T>))
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
        public global::TestProject.GenericDemoDataWithCtorParams<T> ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.GenericDemoDataWithCtorParams<T>, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.GenericDemoDataWithCtorParams<T>>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_GenericDemoDataWithCtorParams<T> : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.GenericDemoDataWithCtorParams<T>>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.GenericDemoDataWithCtorParams<T>>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericDemoDataWithCtorParams<T>> _activator;
        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<T> _copier0;
        private static readonly global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, int> setField0 = (global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericDemoDataWithCtorParams<T>), "<IntValue>k__BackingField");
        private static readonly global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, T> setField1 = (global::System.Action<global::TestProject.GenericDemoDataWithCtorParams<T>, T>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericDemoDataWithCtorParams<T>), "<Value>k__BackingField");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.GenericDemoDataWithCtorParams<T> DeepCopy(global::TestProject.GenericDemoDataWithCtorParams<T> original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.GenericDemoDataWithCtorParams<T> existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.GenericDemoDataWithCtorParams<T>))
                return context.DeepCopy(original);
            var result = _activator.Create();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_GenericDemoDataWithCtorParams(global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericDemoDataWithCtorParams<T>> _activator, global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
            _copier0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<T>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.GenericDemoDataWithCtorParams<T> input, global::TestProject.GenericDemoDataWithCtorParams<T> output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField1(output, _copier0.DeepCopy(input.Value, context));
            setField0(output, input.IntValue);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_GenericDemoDataWithCtorParams<>));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_GenericDemoDataWithCtorParams<>));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
