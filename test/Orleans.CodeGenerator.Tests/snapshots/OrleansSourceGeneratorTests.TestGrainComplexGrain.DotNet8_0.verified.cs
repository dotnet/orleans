[assembly: global::Orleans.Serialization.Configuration.TypeManifestProviderAttribute(typeof(OrleansCodeGen.TestProject.RpcResponseFactories))]
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
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IComplexGrain), "67FE5808")]
    public sealed class Invokable_IComplexGrain_GrainReference_67FE5808 : global::Orleans.Runtime.TaskRequest<global::TestProject.ComplexData>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        public int arg0;
        public string arg1;
        public global::TestProject.ComplexData arg2;
        public global::System.Threading.CancellationToken arg3;
        global::TestProject.IComplexGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IComplexGrain).GetMethod("ProcessData", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, new[] { typeof(int), typeof(string), typeof(global::TestProject.ComplexData), typeof(global::System.Threading.CancellationToken) }, null);
        global::System.Threading.CancellationTokenSource _cts;
        public override int GetArgumentCount() => 4;
        public override string GetMethodName() => "ProcessData";
        public override string GetInterfaceName() => "TestProject.IComplexGrain";
        public override string GetActivityName() => "IComplexGrain/ProcessData";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IComplexGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder)
        {
            _target = (global::TestProject.IComplexGrain)holder.GetTarget();
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
            if (_cts is { } cts)
            {
                cts.Cancel(false);
                return true;
            }

            return false;
        }

        public override bool IsCancellable => true;
        async global::System.Threading.Tasks.ValueTask<global::Orleans.Serialization.Invocation.Response> global::Orleans.Serialization.Invocation.IResponseInvokable.InvokeAndCopy(global::Orleans.Serialization.Serializers.ICodecProvider provider, global::Orleans.Serialization.Cloning.CopyContextPool contexts, global::Orleans.Serialization.DeepCopier<global::Orleans.Serialization.Invocation.Response> responseCopier)
        {
            try
            {
                var factory = global::OrleansCodeGen.TestProject.RpcResponse_FC7DD5BDFactory.Resolve(provider);
                if (!factory.IsSupported)
                    return responseCopier.Copy(await Invoke());
                global::TestProject.ComplexData value = await InvokeInner();
                return factory.RentCopied(value, contexts);
            }
            catch (global::System.Exception exception)
            {
                return global::Orleans.Serialization.Invocation.Response.FromException(exception);
            }
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IComplexGrain : global::Orleans.Runtime.GrainReference, global::TestProject.IComplexGrain
    {
        private readonly OrleansCodeGen.TestProject.Copier_ComplexData _copier_ComplexData_765A40ED2309AF35;
        public Proxy_IComplexGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
            _copier_ComplexData_765A40ED2309AF35 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_ComplexData>(this, CodecProvider);
        }

        global::System.Threading.Tasks.Task<global::TestProject.ComplexData> global::TestProject.IComplexGrain.ProcessData(int arg0, string arg1, global::TestProject.ComplexData arg2, global::System.Threading.CancellationToken arg3)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808();
            request.arg0 = arg0;
            request.arg1 = arg1;
            using var copyContext = base.CopyContextPool.GetContext();
            request.arg2 = _copier_ComplexData_765A40ED2309AF35.DeepCopy(arg2, copyContext);
            request.arg3 = arg3;
            return base.InvokeAsync<global::TestProject.ComplexData>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
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

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
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

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_ComplexData : global::Orleans.Serialization.Activators.IActivator<global::TestProject.ComplexData>
    {
        public global::TestProject.ComplexData Create() => new global::TestProject.ComplexData();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
    public sealed class Codec_Invokable_IComplexGrain_GrainReference_67FE5808 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808);
        private readonly global::System.Type _type_ComplexData_765A40ED2309AF35 = typeof(global::TestProject.ComplexData);
        private readonly OrleansCodeGen.TestProject.Codec_ComplexData _codec_ComplexData_765A40ED2309AF35;
        public Codec_Invokable_IComplexGrain_GrainReference_67FE5808(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _codec_ComplexData_765A40ED2309AF35 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Codec_ComplexData>(this, codecProvider);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.arg0);
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 1U, instance.arg1);
            _codec_ComplexData_765A40ED2309AF35.WriteField(ref writer, 1U, _type_ComplexData_765A40ED2309AF35, instance.arg2);
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
                    instance.arg2 = _codec_ComplexData_765A40ED2309AF35.ReadValue(ref reader, header);
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

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
    public sealed class Copier_Invokable_IComplexGrain_GrainReference_67FE5808 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808>
    {
        private readonly OrleansCodeGen.TestProject.Copier_ComplexData _copier_ComplexData_765A40ED2309AF35;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 DeepCopy(OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808();
            result.arg0 = original.arg0;
            result.arg1 = original.arg1;
            result.arg2 = _copier_ComplexData_765A40ED2309AF35.DeepCopy(original.arg2, context);
            result.arg3 = original.arg3;
            return result;
        }

        public Copier_Invokable_IComplexGrain_GrainReference_67FE5808(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider)
        {
            _copier_ComplexData_765A40ED2309AF35 = OrleansGeneratedCodeHelper.GetService<OrleansCodeGen.TestProject.Copier_ComplexData>(this, codecProvider);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
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

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
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

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_ComplexGrain : global::Orleans.Serialization.Activators.IActivator<global::TestProject.ComplexGrain>
    {
        public global::TestProject.ComplexGrain Create() => new global::TestProject.ComplexGrain();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class RpcResponseFactories : global::Orleans.Serialization.SerializerContext
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options)
        {
            options.AddDefaultSerializerService<RpcResponse_FC7DD5BDFactory>(static provider => new RpcResponse_FC7DD5BDFactory(provider));
            options.AddDefaultSerializer<RpcResponse_FC7DD5BD>(static provider => RpcResponse_FC7DD5BDFactory.Resolve(provider), static provider => RpcResponse_FC7DD5BDFactory.Resolve(provider));
            options.AddRawResponseReader<global::TestProject.ComplexData>(static provider => RpcResponse_FC7DD5BDFactory.Resolve(provider));
#if NET5_0_OR_GREATER
            if (global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                return;
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Codec_ComplexData>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCodec<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Codec_ComplexData>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.TestProject.Codec_ComplexData>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Copier_ComplexData>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCopier<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Copier_ComplexData>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.TestProject.Copier_ComplexData>(null !, provider)));
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<global::TestProject.ComplexData>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Codec_ComplexData>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Copier_ComplexData>>(null !, provider));
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.Response<global::TestProject.ComplexData>));
            options.AddDefaultSerializerService<global::OrleansCodeGen.TestProject.Codec_ComplexData>(static provider => new global::OrleansCodeGen.TestProject.Codec_ComplexData());
            options.AddDefaultSerializerService<global::OrleansCodeGen.TestProject.Copier_ComplexData>(static provider => new global::OrleansCodeGen.TestProject.Copier_ComplexData());
            options.AddDefaultSerializer<global::TestProject.ComplexData>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.TestProject.Codec_ComplexData>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.TestProject.Copier_ComplexData>(null !, provider));
            options.AddAllowedType(typeof(global::TestProject.ComplexData));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.Int32Codec>(static provider => new global::Orleans.Serialization.Codecs.Int32Codec());
            options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.ShallowCopier<int>>(static provider => new global::Orleans.Serialization.Cloning.ShallowCopier<int>());
            options.AddDefaultSerializer<int>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.Int32Codec>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<int>>(null !, provider));
            options.AddAllowedType(typeof(int));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.StringCodec>(static provider => new global::Orleans.Serialization.Codecs.StringCodec());
            options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(static provider => new global::Orleans.Serialization.Cloning.ShallowCopier<string>());
            options.AddDefaultSerializer<string>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.StringCodec>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(null !, provider));
            options.AddAllowedType(typeof(string));
            options.AddDefaultSerializerService<ResponseFieldCodec>(static provider => new ResponseFieldCodec());
            options.AddDefaultSerializerService<ResponseFieldCopier>(static provider => new ResponseFieldCopier());
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ResponseFieldCodec>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<ResponseFieldCopier>(null !, provider));
            options.AddDefaultSerializerService<CompletedResponseActivator>(static provider => new CompletedResponseActivator());
            options.AddDefaultSerializerService<global::OrleansCodeGen.Orleans.Serialization.Invocation.Codec_CompletedResponse>(static provider => new global::OrleansCodeGen.Orleans.Serialization.Invocation.Codec_CompletedResponse(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<CompletedResponseActivator>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.ShallowCopier<global::Orleans.Serialization.Invocation.CompletedResponse>>(static provider => new global::Orleans.Serialization.Cloning.ShallowCopier<global::Orleans.Serialization.Invocation.CompletedResponse>());
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.CompletedResponse>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::OrleansCodeGen.Orleans.Serialization.Invocation.Codec_CompletedResponse>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<global::Orleans.Serialization.Invocation.CompletedResponse>>(null !, provider));
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.CompletedResponse));
#endif
        }

        private sealed class CompletedResponseActivator : global::Orleans.Serialization.Activators.IActivator<global::Orleans.Serialization.Invocation.CompletedResponse>
        {
            public CompletedResponseActivator()
            {
            }

            public global::Orleans.Serialization.Invocation.CompletedResponse Create() => global::Orleans.Serialization.Invocation.CompletedResponse.Instance;
        }

        private sealed class ResponseFieldCodec : global::Orleans.Serialization.Serializers.AbstractTypeSerializer<global::Orleans.Serialization.Invocation.Response>
        {
            public ResponseFieldCodec()
            {
            }
        }

        private sealed class ResponseFieldCopier : global::Orleans.Serialization.Cloning.IDeepCopier<global::Orleans.Serialization.Invocation.Response>
        {
            public ResponseFieldCopier()
            {
            }

            [return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull("input")]
            public global::Orleans.Serialization.Invocation.Response DeepCopy(global::Orleans.Serialization.Invocation.Response input, global::Orleans.Serialization.Cloning.CopyContext context)
            {
                if (context is null)
                    throw new global::System.ArgumentNullException(nameof(context));
                if (input is global::Orleans.Serialization.Invocation.CompletedResponse or global::Orleans.Serialization.Invocation.ExceptionResponse)
                    return input;
                return (global::Orleans.Serialization.Invocation.Response)global::Orleans.Serialization.Codecs.ObjectCopier.DeepCopy(input, context);
            }
        }
    }

    internal sealed class RpcResponse_FC7DD5BD : global::Orleans.Serialization.Invocation.Response, global::Orleans.Serialization.Invocation.IRawResponseWriter
    {
        internal global::TestProject.ComplexData Value;
        private RpcResponse_FC7DD5BDFactory _factory;
        public RpcResponse_FC7DD5BD()
        {
        }

        internal static RpcResponse_FC7DD5BD Rent(global::TestProject.ComplexData value, RpcResponse_FC7DD5BDFactory factory)
        {
            var result = global::Orleans.Serialization.Invocation.ResponsePool.GetGenerated<RpcResponse_FC7DD5BD>();
            result.Value = value;
            result._factory = factory;
            return result;
        }

        public override object Result { get => Value; set => Value = (global::TestProject.ComplexData)value; }

        public override global::System.Exception Exception { get => null; set => throw new global::System.InvalidOperationException("Successful response holders contain result values."); }

        public override global::System.Type GetSimpleResultType() => typeof(global::TestProject.ComplexData);
        public override T GetResult<T>()
        {
            if (typeof(T) == typeof(global::TestProject.ComplexData))
                return global::System.Runtime.CompilerServices.Unsafe.As<global::TestProject.ComplexData, T>(ref Value);
            return (T)(object)Value;
        }

        public void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (_factory is null)
                throw new global::System.ObjectDisposedException(GetType().Name);
            _factory.WriteResult(ref writer, Value);
        }

        public override void Dispose()
        {
            if (_factory is null)
                return;
            Value = default;
            _factory = null;
            global::Orleans.Serialization.Invocation.ResponsePool.ReturnGenerated(this);
        }
    }

    internal sealed class RpcResponse_FC7DD5BDFactory : global::Orleans.Serialization.Invocation.ResponseCodec, global::Orleans.Serialization.Codecs.IFieldCodec<RpcResponse_FC7DD5BD>, global::Orleans.Serialization.Cloning.IDeepCopier<RpcResponse_FC7DD5BD>, global::Orleans.Serialization.Invocation.IRawResponseReader
    {
        private readonly global::OrleansCodeGen.TestProject.Codec_ComplexData _codec;
        private readonly global::OrleansCodeGen.TestProject.Copier_ComplexData _copier;
        public bool IsSupported { get; }

        internal static RpcResponse_FC7DD5BDFactory Resolve(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            provider.GetCodec<global::TestProject.ComplexData>();
            provider.GetDeepCopier<global::TestProject.ComplexData>();
            provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::TestProject.ComplexData>>();
            provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::TestProject.ComplexData>>();
            return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<RpcResponse_FC7DD5BDFactory>(null, provider);
        }

        public RpcResponse_FC7DD5BDFactory(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            _codec = provider.GetCodec<global::TestProject.ComplexData>() as global::OrleansCodeGen.TestProject.Codec_ComplexData;
            _copier = provider.GetDeepCopier<global::TestProject.ComplexData>() as global::OrleansCodeGen.TestProject.Copier_ComplexData;
            var responseCodec = provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::TestProject.ComplexData>>();
            var responseCopier = provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::TestProject.ComplexData>>();
            IsSupported = _codec is not null && _copier is not null && (responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Codec_ComplexData> || responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::TestProject.ComplexData, global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.ComplexData>>) && (responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::TestProject.ComplexData, global::OrleansCodeGen.TestProject.Copier_ComplexData> || responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::TestProject.ComplexData, global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.ComplexData>>);
        }

        internal global::Orleans.Serialization.Invocation.Response RentCopied(global::TestProject.ComplexData value, global::Orleans.Serialization.Cloning.CopyContextPool contexts)
        {
            using var context = contexts.GetContext();
            return RpcResponse_FC7DD5BD.Rent(_copier.DeepCopy(value, context), this);
        }

        [return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull("input")]
        public RpcResponse_FC7DD5BD DeepCopy(RpcResponse_FC7DD5BD input, global::Orleans.Serialization.Cloning.CopyContext context) => input is null ? null : RpcResponse_FC7DD5BD.Rent(_copier.DeepCopy(input.Value, context), this);
        internal void WriteResult<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.ComplexData value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteStartObject(0, null, typeof(global::TestProject.ComplexData));
            if (value is not null)
            {
                _codec.WriteField(ref writer, 0, typeof(global::TestProject.ComplexData), value);
            }

            writer.WriteEndObject();
        }

        public override void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, object value) => WriteResult(ref writer, ((RpcResponse_FC7DD5BD)value).Value);
        public override object ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        global::Orleans.Serialization.Invocation.Response global::Orleans.Serialization.Invocation.IRawResponseReader.ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        private RpcResponse_FC7DD5BD ReadResult<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = RpcResponse_FC7DD5BD.Rent(default, this);
            try
            {
                reader.ReadFieldHeader(ref field);
                if (!field.IsEndBaseOrEndObject)
                {
                    result.Value = _codec.ReadValue(ref reader, field);
                    reader.ReadFieldHeader(ref field);
                    reader.ConsumeEndBaseOrEndObject(ref field);
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, RpcResponse_FC7DD5BD value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (value is null)
            {
                global::Orleans.Serialization.Codecs.ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(global::Orleans.Serialization.Invocation.Response<global::TestProject.ComplexData>));
            if (value.Value is not null)
            {
                _codec.WriteField(ref writer, 0, typeof(global::TestProject.ComplexData), value.Value);
            }

            writer.WriteEndObject();
        }

        public RpcResponse_FC7DD5BD ReadValue<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<RpcResponse_FC7DD5BD, TInput>(ref reader, field);
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            return ReadResult(ref reader, ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_ComplexData));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IComplexGrain_GrainReference_67FE5808));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_ComplexGrain));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_ComplexData));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IComplexGrain_GrainReference_67FE5808));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_ComplexGrain));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IComplexGrain));
            config.AddInterface(typeof(global::TestProject.IComplexGrain));
            config.AddInterfaceImplementation(typeof(global::TestProject.ComplexGrain));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_ComplexData));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_ComplexGrain));
            var n1 = config.CompoundTypeAliases.GetOrAdd("inv");
            var n2 = n1.GetOrAdd(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.GetOrAdd(typeof(global::TestProject.IComplexGrain));
            n3.Add("67FE5808", typeof(OrleansCodeGen.TestProject.Invokable_IComplexGrain_GrainReference_67FE5808));
        }
    }
}
#pragma warning restore