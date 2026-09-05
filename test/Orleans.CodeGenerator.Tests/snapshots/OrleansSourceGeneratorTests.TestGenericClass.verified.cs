#pragma warning disable
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

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_GenericData<T> : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.GenericData<T>>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.GenericData<T>>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.GenericData<T>);
        private readonly global::System.Type _type_T_0CA466BDFA032082 = typeof(T);
        private readonly global::Orleans.Serialization.Codecs.IFieldCodec<T> _codec_T_0CA466BDFA032082;
        public Codec_GenericData(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _codec_T_0CA466BDFA032082 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.IFieldCodec<T>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.GenericData<T> instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _codec_T_0CA466BDFA032082.WriteField(ref writer, 0U, _type_T_0CA466BDFA032082, instance.Value);
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, instance.Description);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.GenericData<T> instance)
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
                    instance.Value = _codec_T_0CA466BDFA032082.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    instance.Description = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.GenericData<T> @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.GenericData<T>))
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
        public global::TestProject.GenericData<T> ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.GenericData<T>, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.GenericData<T>();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.GenericData<T>>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_GenericData<T> : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.GenericData<T>>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.GenericData<T>>
    {
        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<T> _copier_T_0CA466BDFA032082;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.GenericData<T> DeepCopy(global::TestProject.GenericData<T> original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.GenericData<T> existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.GenericData<T>))
                return context.DeepCopy(original);
            var result = new global::TestProject.GenericData<T>();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_GenericData(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _copier_T_0CA466BDFA032082 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<T>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.GenericData<T> input, global::TestProject.GenericData<T> output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            output.Value = _copier_T_0CA466BDFA032082.DeepCopy(input.Value, context);
            output.Description = input.Description;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_GenericData<T> : global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericData<T>>
    {
        public global::TestProject.GenericData<T> Create() => new global::TestProject.GenericData<T>();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_ConcreteUsage : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.ConcreteUsage>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.ConcreteUsage>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.ConcreteUsage);
        private readonly global::System.Type _type_GenericData_Int32_9F0DA0207759221D = typeof(global::TestProject.GenericData<int>);
        private readonly OrleansCodeGen.TestProject.Codec_GenericData<int> _codec_GenericData_Int32_9F0DA0207759221D;
        private readonly global::System.Type _type_GenericData_String_6159CB17C72A4E73 = typeof(global::TestProject.GenericData<string>);
        private readonly OrleansCodeGen.TestProject.Codec_GenericData<string> _codec_GenericData_String_6159CB17C72A4E73;
        public Codec_ConcreteUsage(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _codec_GenericData_Int32_9F0DA0207759221D = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Codec_GenericData<int>>(this, codecProvider);
            _codec_GenericData_String_6159CB17C72A4E73 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Codec_GenericData<string>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.ConcreteUsage instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _codec_GenericData_Int32_9F0DA0207759221D.WriteField(ref writer, 0U, _type_GenericData_Int32_9F0DA0207759221D, instance.IntData);
            _codec_GenericData_String_6159CB17C72A4E73.WriteField(ref writer, 1U, _type_GenericData_String_6159CB17C72A4E73, instance.StringData);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.ConcreteUsage instance)
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
                    instance.IntData = _codec_GenericData_Int32_9F0DA0207759221D.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    instance.StringData = _codec_GenericData_String_6159CB17C72A4E73.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.ConcreteUsage @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.ConcreteUsage))
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
        public global::TestProject.ConcreteUsage ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.ConcreteUsage, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.ConcreteUsage();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.ConcreteUsage>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_ConcreteUsage : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.ConcreteUsage>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.ConcreteUsage>
    {
        private readonly OrleansCodeGen.TestProject.Copier_GenericData<int> _copier_GenericData_Int32_9F0DA0207759221D;
        private readonly OrleansCodeGen.TestProject.Copier_GenericData<string> _copier_GenericData_String_6159CB17C72A4E73;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.ConcreteUsage DeepCopy(global::TestProject.ConcreteUsage original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.ConcreteUsage existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.ConcreteUsage))
                return context.DeepCopy(original);
            var result = new global::TestProject.ConcreteUsage();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_ConcreteUsage(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _copier_GenericData_Int32_9F0DA0207759221D = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_GenericData<int>>(this, codecProvider);
            _copier_GenericData_String_6159CB17C72A4E73 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_GenericData<string>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.ConcreteUsage input, global::TestProject.ConcreteUsage output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            output.IntData = _copier_GenericData_Int32_9F0DA0207759221D.DeepCopy(input.IntData, context);
            output.StringData = _copier_GenericData_String_6159CB17C72A4E73.DeepCopy(input.StringData, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_ConcreteUsage : global::Orleans.Serialization.Activators.IActivator<global::TestProject.ConcreteUsage>
    {
        public global::TestProject.ConcreteUsage Create() => new global::TestProject.ConcreteUsage();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            AddImplementationType(config.Serializers, typeof(OrleansCodeGen.TestProject.Codec_GenericData<>));
            AddImplementationType(config.Serializers, typeof(OrleansCodeGen.TestProject.Codec_ConcreteUsage));
            AddImplementationType(config.Copiers, typeof(OrleansCodeGen.TestProject.Copier_GenericData<>));
            AddImplementationType(config.Copiers, typeof(OrleansCodeGen.TestProject.Copier_ConcreteUsage));
            AddImplementationType(config.Activators, typeof(OrleansCodeGen.TestProject.Activator_GenericData<>));
            AddImplementationType(config.Activators, typeof(OrleansCodeGen.TestProject.Activator_ConcreteUsage));
        }
    }
}
#pragma warning restore