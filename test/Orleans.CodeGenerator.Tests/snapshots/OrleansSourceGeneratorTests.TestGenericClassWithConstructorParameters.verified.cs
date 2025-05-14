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
    public sealed class Codec_GenericWithCtor<T> : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.GenericWithCtor<T>>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.GenericWithCtor<T>>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.GenericWithCtor<T>);
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericWithCtor<T>> _activator;
        private readonly global::System.Type _type0 = typeof(T);
        private readonly global::Orleans.Serialization.Codecs.IFieldCodec<T> _codec0;
        private static readonly global::System.Func<global::TestProject.GenericWithCtor<T>, int> getField0 = (global::System.Func<global::TestProject.GenericWithCtor<T>, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.GenericWithCtor<T>), "_id");
        private static readonly global::System.Action<global::TestProject.GenericWithCtor<T>, int> setField0 = (global::System.Action<global::TestProject.GenericWithCtor<T>, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericWithCtor<T>), "_id");
        private static readonly global::System.Func<global::TestProject.GenericWithCtor<T>, T> getField1 = (global::System.Func<global::TestProject.GenericWithCtor<T>, T>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.GenericWithCtor<T>), "_value");
        private static readonly global::System.Action<global::TestProject.GenericWithCtor<T>, T> setField1 = (global::System.Action<global::TestProject.GenericWithCtor<T>, T>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericWithCtor<T>), "_value");
        public Codec_GenericWithCtor(global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericWithCtor<T>> _activator, global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
            _codec0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.IFieldCodec<T>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.GenericWithCtor<T> instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _codec0.WriteField(ref writer, 0U, _type0, getField1(instance));
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, getField0(instance));
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.GenericWithCtor<T> instance)
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
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.GenericWithCtor<T> @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.GenericWithCtor<T>))
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
        public global::TestProject.GenericWithCtor<T> ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.GenericWithCtor<T>, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.GenericWithCtor<T>>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_GenericWithCtor<T> : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.GenericWithCtor<T>>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.GenericWithCtor<T>>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericWithCtor<T>> _activator;
        private readonly global::Orleans.Serialization.Cloning.IDeepCopier<T> _copier0;
        private static readonly global::System.Func<global::TestProject.GenericWithCtor<T>, int> getField0 = (global::System.Func<global::TestProject.GenericWithCtor<T>, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.GenericWithCtor<T>), "_id");
        private static readonly global::System.Action<global::TestProject.GenericWithCtor<T>, int> setField0 = (global::System.Action<global::TestProject.GenericWithCtor<T>, int>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericWithCtor<T>), "_id");
        private static readonly global::System.Func<global::TestProject.GenericWithCtor<T>, T> getField1 = (global::System.Func<global::TestProject.GenericWithCtor<T>, T>)global::Orleans.Serialization.Utilities.FieldAccessor.GetGetter(typeof(global::TestProject.GenericWithCtor<T>), "_value");
        private static readonly global::System.Action<global::TestProject.GenericWithCtor<T>, T> setField1 = (global::System.Action<global::TestProject.GenericWithCtor<T>, T>)global::Orleans.Serialization.Utilities.FieldAccessor.GetReferenceSetter(typeof(global::TestProject.GenericWithCtor<T>), "_value");
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.GenericWithCtor<T> DeepCopy(global::TestProject.GenericWithCtor<T> original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.GenericWithCtor<T> existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.GenericWithCtor<T>))
                return context.DeepCopy(original);
            var result = _activator.Create();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_GenericWithCtor(global::Orleans.Serialization.Activators.IActivator<global::TestProject.GenericWithCtor<T>> _activator, global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
            _copier0 = OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.IDeepCopier<T>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.GenericWithCtor<T> input, global::TestProject.GenericWithCtor<T> output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            setField1(output, _copier0.DeepCopy(getField1(input), context));
            setField0(output, getField0(input));
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_UsesGenericWithCtor : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.UsesGenericWithCtor>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.UsesGenericWithCtor>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.UsesGenericWithCtor);
        private readonly global::System.Type _type0 = typeof(global::TestProject.GenericWithCtor<string>);
        private readonly OrleansCodeGen.TestProject.Codec_GenericWithCtor<string> _codec0;
        public Codec_UsesGenericWithCtor(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _codec0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Codec_GenericWithCtor<string>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.UsesGenericWithCtor instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _codec0.WriteField(ref writer, 0U, _type0, instance.StringGen);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.UsesGenericWithCtor instance)
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
                    instance.StringGen = _codec0.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.UsesGenericWithCtor @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.UsesGenericWithCtor))
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
        public global::TestProject.UsesGenericWithCtor ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.UsesGenericWithCtor, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.UsesGenericWithCtor();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.UsesGenericWithCtor>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_UsesGenericWithCtor : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.UsesGenericWithCtor>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.UsesGenericWithCtor>
    {
        private readonly OrleansCodeGen.TestProject.Copier_GenericWithCtor<string> _copier0;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.UsesGenericWithCtor DeepCopy(global::TestProject.UsesGenericWithCtor original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.UsesGenericWithCtor existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.UsesGenericWithCtor))
                return context.DeepCopy(original);
            var result = new global::TestProject.UsesGenericWithCtor();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_UsesGenericWithCtor(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _copier0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_GenericWithCtor<string>>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.UsesGenericWithCtor input, global::TestProject.UsesGenericWithCtor output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            output.StringGen = _copier0.DeepCopy(input.StringGen, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_UsesGenericWithCtor : global::Orleans.Serialization.Activators.IActivator<global::TestProject.UsesGenericWithCtor>
    {
        public global::TestProject.UsesGenericWithCtor Create() => new global::TestProject.UsesGenericWithCtor();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_GenericWithCtor<>));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_UsesGenericWithCtor));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_GenericWithCtor<>));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_UsesGenericWithCtor));
            config.Activators.Add(typeof(OrleansCodeGen.TestProject.Activator_UsesGenericWithCtor));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
