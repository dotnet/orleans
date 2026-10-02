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
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IBasicGrain), "6B0E24A1")]
    public sealed class Invokable_IBasicGrain_GrainReference_6B0E24A1 : global::Orleans.Runtime.TaskRequest<string>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        public string arg0;
        global::TestProject.IBasicGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IBasicGrain).GetMethod("SayHello", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, new[] { typeof(string) }, null);
        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "SayHello";
        public override string GetInterfaceName() => "TestProject.IBasicGrain";
        public override string GetActivityName() => "IBasicGrain/SayHello";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IBasicGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IBasicGrain)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            arg0 = default;
            _target = default;
        }

        public override object GetArgument(int index)
        {
            switch (index)
            {
                case 0:
                    return arg0;
                default:
                    return OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 0);
            }
        }

        public override void SetArgument(int index, object value)
        {
            switch (index)
            {
                case 0:
                    arg0 = (string)value;
                    return;
                default:
                    OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 0);
                    return;
            }
        }

        protected override global::System.Threading.Tasks.Task<string> InvokeInner() => _target.SayHello(arg0);
        async global::System.Threading.Tasks.ValueTask<global::Orleans.Serialization.Invocation.Response> global::Orleans.Serialization.Invocation.IResponseInvokable.InvokeAndCopy(global::Orleans.Serialization.Serializers.ICodecProvider provider, global::Orleans.Serialization.Cloning.CopyContextPool contexts, global::Orleans.Serialization.DeepCopier<global::Orleans.Serialization.Invocation.Response> responseCopier)
        {
            try
            {
                var factory = global::OrleansCodeGen.TestProject.RpcResponse_9146C7E3Factory.Resolve(provider);
                if (!factory.IsSupported)
                    return responseCopier.Copy(await Invoke());
                string value = await InvokeInner();
                return factory.RentCopied(value, contexts);
            }
            catch (global::System.Exception exception)
            {
                return global::Orleans.Serialization.Invocation.Response.FromException(exception);
            }
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IBasicGrain : global::Orleans.Runtime.GrainReference, global::TestProject.IBasicGrain
    {
        public Proxy_IBasicGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<string> global::TestProject.IBasicGrain.SayHello(string arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1();
            request.arg0 = arg0;
            return base.InvokeAsync<string>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
    public sealed class Codec_Invokable_IBasicGrain_GrainReference_6B0E24A1 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1 instance)
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
                    instance.arg0 = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
    public sealed class Copier_Invokable_IBasicGrain_GrainReference_6B0E24A1 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1 DeepCopy(OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1();
            result.arg0 = original.arg0;
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
    public sealed class Codec_BasicGrain : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.BasicGrain>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.BasicGrain>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.BasicGrain);
        private readonly global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer;
        public Codec_BasicGrain(global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer)
        {
            this._baseTypeSerializer = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeSerializer);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.BasicGrain instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _baseTypeSerializer.Serialize(ref writer, instance);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.BasicGrain instance)
        {
            _baseTypeSerializer.Deserialize(ref reader, instance);
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.BasicGrain @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.BasicGrain))
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
        public global::TestProject.BasicGrain ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.BasicGrain, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.BasicGrain();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.BasicGrain>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::System.ComponentModel.DescriptionAttribute("OrleansCodeGen.FieldAccessors.v1:Static")]
    public sealed class Copier_BasicGrain : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.BasicGrain>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.BasicGrain>
    {
        private readonly global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.BasicGrain DeepCopy(global::TestProject.BasicGrain original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.BasicGrain existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.BasicGrain))
                return context.DeepCopy(original);
            var result = new global::TestProject.BasicGrain();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_BasicGrain(global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier)
        {
            this._baseTypeCopier = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeCopier);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.BasicGrain input, global::TestProject.BasicGrain output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            _baseTypeCopier.DeepCopy(input, output, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_BasicGrain : global::Orleans.Serialization.Activators.IActivator<global::TestProject.BasicGrain>
    {
        public global::TestProject.BasicGrain Create() => new global::TestProject.BasicGrain();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class RpcResponseFactories : global::Orleans.Serialization.SerializerContext
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options)
        {
            options.AddDefaultSerializerService<RpcResponse_9146C7E3Factory>(static provider => new RpcResponse_9146C7E3Factory(provider));
            options.AddDefaultSerializer<RpcResponse_9146C7E3>(static provider => RpcResponse_9146C7E3Factory.Resolve(provider), static provider => RpcResponse_9146C7E3Factory.Resolve(provider));
            options.AddRawResponseReader<string>(static provider => RpcResponse_9146C7E3Factory.Resolve(provider));
#if NET5_0_OR_GREATER
            if (global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                return;
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.StringCodec>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(null !, provider)));
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<string>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>>(null !, provider));
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.Response<string>));
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

    internal sealed class RpcResponse_9146C7E3 : global::Orleans.Serialization.Invocation.Response, global::Orleans.Serialization.Invocation.IRawResponseWriter
    {
        internal string Value;
        private RpcResponse_9146C7E3Factory _factory;
        public RpcResponse_9146C7E3()
        {
        }

        internal static RpcResponse_9146C7E3 Rent(string value, RpcResponse_9146C7E3Factory factory)
        {
            var result = global::Orleans.Serialization.Invocation.ResponsePool.GetGenerated<RpcResponse_9146C7E3>();
            result.Value = value;
            result._factory = factory;
            return result;
        }

        public override object Result { get => Value; set => Value = (string)value; }

        public override global::System.Exception Exception { get => null; set => throw new global::System.InvalidOperationException("Successful response holders contain result values."); }

        public override global::System.Type GetSimpleResultType() => typeof(string);
        public override T GetResult<T>()
        {
            if (typeof(T) == typeof(string))
                return global::System.Runtime.CompilerServices.Unsafe.As<string, T>(ref Value);
            return (T)(object)Value;
        }

        public void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (_factory is null)
                throw new global::System.ObjectDisposedException(GetType().Name);
            writer.WriteStartObject(0, null, typeof(string));
            if (Value is not null)
            {
                global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0, Value);
            }

            writer.WriteEndObject();
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

    internal sealed class RpcResponse_9146C7E3Factory : global::Orleans.Serialization.Invocation.ResponseCodec, global::Orleans.Serialization.Codecs.IFieldCodec<RpcResponse_9146C7E3>, global::Orleans.Serialization.Cloning.IDeepCopier<RpcResponse_9146C7E3>, global::Orleans.Serialization.Invocation.IRawResponseReader
    {
        private readonly global::Orleans.Serialization.Codecs.StringCodec _codec;
        private readonly global::Orleans.Serialization.Cloning.ShallowCopier<string> _copier;
        public bool IsSupported { get; }

        internal static RpcResponse_9146C7E3Factory Resolve(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            provider.GetCodec<string>();
            provider.GetDeepCopier<string>();
            provider.GetCodec<global::Orleans.Serialization.Invocation.Response<string>>();
            provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<string>>();
            return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<RpcResponse_9146C7E3Factory>(null, provider);
        }

        public RpcResponse_9146C7E3Factory(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            _codec = provider.GetCodec<string>() as global::Orleans.Serialization.Codecs.StringCodec;
            _copier = provider.GetDeepCopier<string>() as global::Orleans.Serialization.Cloning.ShallowCopier<string>;
            var responseCodec = provider.GetCodec<global::Orleans.Serialization.Invocation.Response<string>>();
            var responseCopier = provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<string>>();
            IsSupported = _codec is not null && _copier is not null && (responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec> || responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.IFieldCodec<string>>) && (responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>> || responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.IDeepCopier<string>>);
        }

        internal global::Orleans.Serialization.Invocation.Response RentCopied(string value, global::Orleans.Serialization.Cloning.CopyContextPool contexts)
        {
            return RpcResponse_9146C7E3.Rent(value, this);
        }

        [return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull("input")]
        public RpcResponse_9146C7E3 DeepCopy(RpcResponse_9146C7E3 input, global::Orleans.Serialization.Cloning.CopyContext context) => input is null ? null : RpcResponse_9146C7E3.Rent(input.Value, this);
        internal void WriteResult<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, string value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteStartObject(0, null, typeof(string));
            if (value is not null)
            {
                global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0, value);
            }

            writer.WriteEndObject();
        }

        public override void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, object value) => WriteResult(ref writer, ((RpcResponse_9146C7E3)value).Value);
        public override object ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        global::Orleans.Serialization.Invocation.Response global::Orleans.Serialization.Invocation.IRawResponseReader.ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        private RpcResponse_9146C7E3 ReadResult<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = RpcResponse_9146C7E3.Rent(default, this);
            try
            {
                reader.ReadFieldHeader(ref field);
                if (!field.IsEndBaseOrEndObject)
                {
                    result.Value = global::Orleans.Serialization.Codecs.StringCodec.ReadValue(ref reader, field);
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

        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, RpcResponse_9146C7E3 value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (value is null)
            {
                global::Orleans.Serialization.Codecs.ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(global::Orleans.Serialization.Invocation.Response<string>));
            if (value.Value is not null)
            {
                global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0, value.Value);
            }

            writer.WriteEndObject();
        }

        public RpcResponse_9146C7E3 ReadValue<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<RpcResponse_9146C7E3, TInput>(ref reader, field);
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            return ReadResult(ref reader, ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IBasicGrain_GrainReference_6B0E24A1));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_BasicGrain));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IBasicGrain_GrainReference_6B0E24A1));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_BasicGrain));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IBasicGrain));
            config.AddInterface(typeof(global::TestProject.IBasicGrain));
            config.AddInterfaceImplementation(typeof(global::TestProject.BasicGrain));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_BasicGrain));
            var n1 = config.CompoundTypeAliases.GetOrAdd("inv");
            var n2 = n1.GetOrAdd(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.GetOrAdd(typeof(global::TestProject.IBasicGrain));
            n3.Add("6B0E24A1", typeof(OrleansCodeGen.TestProject.Invokable_IBasicGrain_GrainReference_6B0E24A1));
        }
    }
}
#pragma warning restore