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
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IComplexGrain), "67FE5808")]
    public sealed class Invokable_IComplexGrain_GrainReference_67FE5808 : global::Orleans.Runtime.TaskRequest<global::TestProject.ComplexData>
    {
        public int arg0;
        public string arg1;
        public global::TestProject.ComplexData arg2;
        public global::System.Threading.CancellationToken arg3;
        global::TestProject.IComplexGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IComplexGrain), "ProcessData", null, new[] { typeof(int), typeof(string), typeof(global::TestProject.ComplexData), typeof(global::System.Threading.CancellationToken) });
        global::System.Threading.CancellationTokenSource _cts;
        public override int GetArgumentCount() => 4;
        public override string GetMethodName() => "ProcessData";
        public override string GetInterfaceName() => "TestProject.IComplexGrain";
        public override string GetActivityName() => "IComplexGrain/ProcessData";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IComplexGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder)
        {
            _target = holder.GetTarget<global::TestProject.IComplexGrain>();
            _cts = new();
            arg3 = _cts.Token;
        }

        public override object GetTarget() => _target;
        public override void Dispose()
        {
            arg0 = default;
            arg1 = default;
            arg2 = default;
            arg3 = default;
            _target = default;
            _cts?.Dispose();
            _cts = default;
        }

        public override object GetArgument(int index)
        {
            switch (index)
            {
                case 0:
                    return arg0;
                case 1:
                    return arg1;
                case 2:
                    return arg2;
                case 3:
                    return arg3;
                default:
                    return OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 3);
            }
        }

        public override void SetArgument(int index, object value)
        {
            switch (index)
            {
                case 0:
                    arg0 = (int)value;
                    return;
                case 1:
                    arg1 = (string)value;
                    return;
                case 2:
                    arg2 = (global::TestProject.ComplexData)value;
                    return;
                case 3:
                    arg3 = (global::System.Threading.CancellationToken)value;
                    return;
                default:
                    OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 3);
                    return;
            }
        }

        protected override global::System.Threading.Tasks.Task<global::TestProject.ComplexData> InvokeInner() => _target.ProcessData(arg0, arg1, arg2, arg3);
        public override global::System.Threading.CancellationToken GetCancellationToken() => arg3;
        public override bool TryCancel()
        {
            _cts?.Cancel(false);
            return true;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IComplexGrain : global::Orleans.Runtime.GrainReference, global::TestProject.IComplexGrain
    {
        private readonly OrleansCodeGen.TestProject.Copier_ComplexData _copier0;
        public Proxy_IComplexGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
            _copier0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_ComplexData>(this, CodecProvider);
        }

        global::System.Threading.Tasks.Task<global::TestProject.ComplexData> global::TestProject.IComplexGrain.ProcessData(int arg0, string arg1, global::TestProject.ComplexData arg2, global::System.Threading.CancellationToken arg3)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808();
            request.arg0 = arg0;
            request.arg1 = arg1;
            using var copyContext = base.CopyContextPool.GetContext();
            request.arg2 = _copier0.DeepCopy(arg2, copyContext);
            request.arg3 = arg3;
            return base.InvokeAsync<global::TestProject.ComplexData>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_ComplexData : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.ComplexData>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.ComplexData>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.ComplexData);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.ComplexData instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.IntValue);
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, instance.StringValue);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.ComplexData instance)
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
                    instance.IntValue = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    instance.StringValue = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.ComplexData @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.ComplexData))
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
        public global::TestProject.ComplexData ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.ComplexData, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.ComplexData();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.ComplexData>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_ComplexData : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.ComplexData>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.ComplexData>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.ComplexData DeepCopy(global::TestProject.ComplexData original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.ComplexData existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.ComplexData))
                return context.DeepCopy(original);
            var result = new global::TestProject.ComplexData();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.ComplexData input, global::TestProject.ComplexData output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            output.IntValue = input.IntValue;
            output.StringValue = input.StringValue;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_ComplexData : global::Orleans.Serialization.Activators.IActivator<global::TestProject.ComplexData>
    {
        public global::TestProject.ComplexData Create() => new global::TestProject.ComplexData();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IComplexGrain_GrainReference_67FE5808 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808);
        private readonly global::System.Type _type0 = typeof(global::TestProject.ComplexData);
        private readonly OrleansCodeGen.TestProject.Codec_ComplexData _codec0;
        public Codec_Invokable_IComplexGrain_GrainReference_67FE5808(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _codec0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Codec_ComplexData>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.arg0);
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, instance.arg1);
            _codec0.WriteField(ref writer, 1U, _type0, instance.arg2);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 instance)
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
                    instance.arg0 = global::Orleans.Serialization.Codecs.Int32Codec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 1U)
                {
                    instance.arg1 = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                    if (header.IsEndBaseOrEndObject)
                        break;
                    id += header.FieldIdDelta;
                }

                if (id == 2U)
                {
                    instance.arg2 = _codec0.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            Serialize(ref writer, @value);
            writer.WriteEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IComplexGrain_GrainReference_67FE5808 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808>
    {
        private readonly OrleansCodeGen.TestProject.Copier_ComplexData _copier0;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 DeepCopy(OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808();
            result.arg0 = original.arg0;
            result.arg1 = original.arg1;
            result.arg2 = _copier0.DeepCopy(original.arg2, context);
            result.arg3 = original.arg3;
            return result;
        }

        public Copier_Invokable_IComplexGrain_GrainReference_67FE5808(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _copier0 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_ComplexData>(this, codecProvider);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_ComplexGrain : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.ComplexGrain>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.ComplexGrain>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.ComplexGrain);
        private readonly global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer;
        public Codec_ComplexGrain(global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer)
        {
            this._baseTypeSerializer = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeSerializer);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.ComplexGrain instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _baseTypeSerializer.Serialize(ref writer, instance);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.ComplexGrain instance)
        {
            _baseTypeSerializer.Deserialize(ref reader, instance);
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.ComplexGrain @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.ComplexGrain))
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
        public global::TestProject.ComplexGrain ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.ComplexGrain, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.ComplexGrain();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.ComplexGrain>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_ComplexGrain : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.ComplexGrain>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.ComplexGrain>
    {
        private readonly global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.ComplexGrain DeepCopy(global::TestProject.ComplexGrain original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.ComplexGrain existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.ComplexGrain))
                return context.DeepCopy(original);
            var result = new global::TestProject.ComplexGrain();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_ComplexGrain(global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier)
        {
            this._baseTypeCopier = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeCopier);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.ComplexGrain input, global::TestProject.ComplexGrain output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            _baseTypeCopier.DeepCopy(input, output, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_ComplexGrain : global::Orleans.Serialization.Activators.IActivator<global::TestProject.ComplexGrain>
    {
        public global::TestProject.ComplexGrain Create() => new global::TestProject.ComplexGrain();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_ComplexData));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IComplexGrain_GrainReference_67FE5808));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_ComplexGrain));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_ComplexData));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IComplexGrain_GrainReference_67FE5808));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_ComplexGrain));
            config.InterfaceProxies.Add(typeof(OrleansCodeGen.TestProject.Proxy_IComplexGrain));
            config.Interfaces.Add(typeof(global::TestProject.IComplexGrain));
            config.InterfaceImplementations.Add(typeof(global::TestProject.ComplexGrain));
            config.Activators.Add(typeof(OrleansCodeGen.TestProject.Activator_ComplexData));
            config.Activators.Add(typeof(OrleansCodeGen.TestProject.Activator_ComplexGrain));
            var n1 = config.CompoundTypeAliases.Add("inv");
            var n2 = n1.Add(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.Add(typeof(global::TestProject.IComplexGrain));
            n3.Add("67FE5808", typeof(OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
