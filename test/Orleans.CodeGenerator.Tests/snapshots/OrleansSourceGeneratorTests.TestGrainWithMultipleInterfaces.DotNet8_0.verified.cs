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
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IGrainA), "11405B98")]
    public sealed class Invokable_IGrainA_GrainReference_11405B98 : global::Orleans.Runtime.TaskRequest<string>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        public string arg0;
        global::TestProject.IGrainA _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IGrainA).GetMethod("MethodA", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, new[] { typeof(string) }, null);
        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "MethodA";
        public override string GetInterfaceName() => "TestProject.IGrainA";
        public override string GetActivityName() => "IGrainA/MethodA";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IGrainA);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IGrainA)holder.GetTarget();
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

        protected override global::System.Threading.Tasks.Task<string> InvokeInner() => _target.MethodA(arg0);
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
    internal sealed class Proxy_IGrainA : global::Orleans.Runtime.GrainReference, global::TestProject.IGrainA
    {
        public Proxy_IGrainA(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<string> global::TestProject.IGrainA.MethodA(string arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98();
            request.arg0 = arg0;
            return base.InvokeAsync<string>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IGrainB), "6B5D7809")]
    public sealed class Invokable_IGrainB_GrainReference_6B5D7809 : global::Orleans.Runtime.TaskRequest<string>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        public string arg0;
        global::TestProject.IGrainB _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IGrainB).GetMethod("MethodB", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, new[] { typeof(string) }, null);
        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "MethodB";
        public override string GetInterfaceName() => "TestProject.IGrainB";
        public override string GetActivityName() => "IGrainB/MethodB";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IGrainB);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IGrainB)holder.GetTarget();
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

        protected override global::System.Threading.Tasks.Task<string> InvokeInner() => _target.MethodB(arg0);
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
    internal sealed class Proxy_IGrainB : global::Orleans.Runtime.GrainReference, global::TestProject.IGrainB
    {
        public Proxy_IGrainB(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<string> global::TestProject.IGrainB.MethodB(string arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809();
            request.arg0 = arg0;
            return base.InvokeAsync<string>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IGrainA_GrainReference_11405B98 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98 instance)
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
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IGrainA_GrainReference_11405B98 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98 DeepCopy(OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98();
            result.arg0 = original.arg0;
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IGrainB_GrainReference_6B5D7809 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809 instance)
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
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IGrainB_GrainReference_6B5D7809 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809 DeepCopy(OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809();
            result.arg0 = original.arg0;
            return result;
        }
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
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.StringCodec>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(null !, provider)));
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<string>, global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>, global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>>(null !, provider), codecDependencies: new global::System.Type[] { typeof(global::Orleans.Serialization.Codecs.IFieldCodec<string>) }, copierDependencies: new global::System.Type[] { typeof(global::Orleans.Serialization.Cloning.IDeepCopier<string>) });
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.Response<string>));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.StringCodec>(static provider => new global::Orleans.Serialization.Codecs.StringCodec());
            options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(static provider => new global::Orleans.Serialization.Cloning.ShallowCopier<string>());
            options.AddDefaultSerializer<string, global::Orleans.Serialization.Codecs.StringCodec, global::Orleans.Serialization.Cloning.ShallowCopier<string>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.StringCodec>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(null !, provider));
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
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IGrainA_GrainReference_11405B98), typeof(OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IGrainB_GrainReference_6B5D7809), typeof(OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IGrainA_GrainReference_11405B98), typeof(OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IGrainB_GrainReference_6B5D7809), typeof(OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IGrainA));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IGrainB));
            config.AddInterface(typeof(global::TestProject.IGrainA));
            config.AddInterface(typeof(global::TestProject.IGrainB));
            config.AddInterfaceImplementation(typeof(global::TestProject.RealGrain));
            var n1 = config.CompoundTypeAliases.GetOrAdd("inv");
            var n2 = n1.GetOrAdd(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.GetOrAdd(typeof(global::TestProject.IGrainA));
            n3.Add("11405B98", typeof(OrleansCodeGen.TestProject.Invokable_IGrainA_GrainReference_11405B98));
            var n5 = n2.GetOrAdd(typeof(global::TestProject.IGrainB));
            n5.Add("6B5D7809", typeof(OrleansCodeGen.TestProject.Invokable_IGrainB_GrainReference_6B5D7809));
        }
    }
}
#pragma warning restore