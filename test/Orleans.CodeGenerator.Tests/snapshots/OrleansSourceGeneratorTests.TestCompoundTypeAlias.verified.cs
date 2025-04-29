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
    public sealed class Codec_MyCompoundTypeAliasBaseClass : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.MyCompoundTypeAliasBaseClass>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.MyCompoundTypeAliasBaseClass>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.MyCompoundTypeAliasBaseClass);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.MyCompoundTypeAliasBaseClass instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.BaseValue);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.MyCompoundTypeAliasBaseClass instance)
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
                    instance.BaseValue = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.MyCompoundTypeAliasBaseClass @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.MyCompoundTypeAliasBaseClass))
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
        public global::TestProject.MyCompoundTypeAliasBaseClass ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.MyCompoundTypeAliasBaseClass, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.MyCompoundTypeAliasBaseClass();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.MyCompoundTypeAliasBaseClass>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_MyCompoundTypeAliasBaseClass : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.MyCompoundTypeAliasBaseClass>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.MyCompoundTypeAliasBaseClass>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.MyCompoundTypeAliasBaseClass DeepCopy(global::TestProject.MyCompoundTypeAliasBaseClass original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.MyCompoundTypeAliasBaseClass existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.MyCompoundTypeAliasBaseClass))
                return context.DeepCopy(original);
            var result = new global::TestProject.MyCompoundTypeAliasBaseClass();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.MyCompoundTypeAliasBaseClass input, global::TestProject.MyCompoundTypeAliasBaseClass output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            output.BaseValue = input.BaseValue;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_MyCompoundTypeAliasBaseClass : global::Orleans.Serialization.Activators.IActivator<global::TestProject.MyCompoundTypeAliasBaseClass>
    {
        public global::TestProject.MyCompoundTypeAliasBaseClass Create() => new global::TestProject.MyCompoundTypeAliasBaseClass();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_MyCompoundTypeAliasClass : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.MyCompoundTypeAliasClass>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.MyCompoundTypeAliasClass>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.MyCompoundTypeAliasClass);
        private readonly OrleansCodeGen.TestProject.Codec_MyCompoundTypeAliasBaseClass _baseTypeSerializer;
        public Codec_MyCompoundTypeAliasClass(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _baseTypeSerializer = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Codec_MyCompoundTypeAliasBaseClass>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.MyCompoundTypeAliasClass instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _baseTypeSerializer.Serialize(ref writer, instance);
            writer.WriteEndBase();
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.Name);
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 1U, instance.Value);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.MyCompoundTypeAliasClass instance)
        {
            _baseTypeSerializer.Deserialize(ref reader, instance);
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
                    instance.Name = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    instance.Value = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.MyCompoundTypeAliasClass @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.MyCompoundTypeAliasClass))
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
        public global::TestProject.MyCompoundTypeAliasClass ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.MyCompoundTypeAliasClass, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.MyCompoundTypeAliasClass();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.MyCompoundTypeAliasClass>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_MyCompoundTypeAliasClass : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.MyCompoundTypeAliasClass>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.MyCompoundTypeAliasClass>
    {
        private readonly OrleansCodeGen.TestProject.Copier_MyCompoundTypeAliasBaseClass _baseTypeCopier;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.MyCompoundTypeAliasClass DeepCopy(global::TestProject.MyCompoundTypeAliasClass original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.MyCompoundTypeAliasClass existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.MyCompoundTypeAliasClass))
                return context.DeepCopy(original);
            var result = new global::TestProject.MyCompoundTypeAliasClass();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_MyCompoundTypeAliasClass(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _baseTypeCopier = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_MyCompoundTypeAliasBaseClass>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.MyCompoundTypeAliasClass input, global::TestProject.MyCompoundTypeAliasClass output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            _baseTypeCopier.DeepCopy(input, output, context);
            output.Name = input.Name;
            output.Value = input.Value;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_MyCompoundTypeAliasClass : global::Orleans.Serialization.Activators.IActivator<global::TestProject.MyCompoundTypeAliasClass>
    {
        public global::TestProject.MyCompoundTypeAliasClass Create() => new global::TestProject.MyCompoundTypeAliasClass();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_MyCompoundTypeAliasBaseClass));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_MyCompoundTypeAliasClass));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_MyCompoundTypeAliasBaseClass));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_MyCompoundTypeAliasClass));
            config.Activators.Add(typeof(OrleansCodeGen.TestProject.Activator_MyCompoundTypeAliasBaseClass));
            config.Activators.Add(typeof(OrleansCodeGen.TestProject.Activator_MyCompoundTypeAliasClass));
            config.WellKnownTypeAliases.Add("_custom_type_alias_", typeof(global::TestProject.MyTypeAliasClass));
            var n1 = config.CompoundTypeAliases.Add("xx_test_xx");
            var n2 = n1.Add(typeof(global::TestProject.MyTypeAliasClass));
            var n3 = n2.Add(typeof( int ));
            n3.Add("1", typeof(global::TestProject.MyCompoundTypeAliasClass));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
