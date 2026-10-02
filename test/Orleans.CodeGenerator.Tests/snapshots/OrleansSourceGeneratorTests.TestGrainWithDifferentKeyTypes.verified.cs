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
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IMyGrainWithGuidKey), "8F0FEC0E")]
    public sealed class Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E : global::Orleans.Runtime.TaskRequest<global::System.Guid>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        global::TestProject.IMyGrainWithGuidKey _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IMyGrainWithGuidKey).GetMethod("GetGuidValue", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, global::System.Type.EmptyTypes, null);
        public override string GetMethodName() => "GetGuidValue";
        public override string GetInterfaceName() => "TestProject.IMyGrainWithGuidKey";
        public override string GetActivityName() => "IMyGrainWithGuidKey/GetGuidValue";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IMyGrainWithGuidKey);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IMyGrainWithGuidKey)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            _target = default;
        }

        protected override global::System.Threading.Tasks.Task<global::System.Guid> InvokeInner() => _target.GetGuidValue();
        async global::System.Threading.Tasks.ValueTask<global::Orleans.Serialization.Invocation.Response> global::Orleans.Serialization.Invocation.IResponseInvokable.InvokeAndCopy(global::Orleans.Serialization.Serializers.ICodecProvider provider, global::Orleans.Serialization.Cloning.CopyContextPool contexts, global::Orleans.Serialization.DeepCopier<global::Orleans.Serialization.Invocation.Response> responseCopier)
        {
            try
            {
                var factory = global::OrleansCodeGen.TestProject.RpcResponse_5C3A711CFactory.Resolve(provider);
                if (!factory.IsSupported)
                    return responseCopier.Copy(await Invoke());
                global::System.Guid value = await InvokeInner();
                return factory.RentCopied(value, contexts);
            }
            catch (global::System.Exception exception)
            {
                return global::Orleans.Serialization.Invocation.Response.FromException(exception);
            }
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IMyGrainWithGuidKey : global::Orleans.Runtime.GrainReference, global::TestProject.IMyGrainWithGuidKey
    {
        public Proxy_IMyGrainWithGuidKey(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<global::System.Guid> global::TestProject.IMyGrainWithGuidKey.GetGuidValue()
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E();
            return base.InvokeAsync<global::System.Guid>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IMyGrainWithStringKey), "43570316")]
    public sealed class Invokable_IMyGrainWithStringKey_GrainReference_43570316 : global::Orleans.Runtime.TaskRequest<string>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        global::TestProject.IMyGrainWithStringKey _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IMyGrainWithStringKey).GetMethod("GetStringKey", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, global::System.Type.EmptyTypes, null);
        public override string GetMethodName() => "GetStringKey";
        public override string GetInterfaceName() => "TestProject.IMyGrainWithStringKey";
        public override string GetActivityName() => "IMyGrainWithStringKey/GetStringKey";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IMyGrainWithStringKey);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IMyGrainWithStringKey)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            _target = default;
        }

        protected override global::System.Threading.Tasks.Task<string> InvokeInner() => _target.GetStringKey();
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
    internal sealed class Proxy_IMyGrainWithStringKey : global::Orleans.Runtime.GrainReference, global::TestProject.IMyGrainWithStringKey
    {
        public Proxy_IMyGrainWithStringKey(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<string> global::TestProject.IMyGrainWithStringKey.GetStringKey()
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316();
            return base.InvokeAsync<string>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IMyGrainWithGuidCompoundKey), "A9FEF7AF")]
    public sealed class Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF : global::Orleans.Runtime.TaskRequest<global::System.Tuple<global::System.Guid, string>>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        global::TestProject.IMyGrainWithGuidCompoundKey _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IMyGrainWithGuidCompoundKey).GetMethod("GetGuidAndStringKey", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, global::System.Type.EmptyTypes, null);
        public override string GetMethodName() => "GetGuidAndStringKey";
        public override string GetInterfaceName() => "TestProject.IMyGrainWithGuidCompoundKey";
        public override string GetActivityName() => "IMyGrainWithGuidCompoundKey/GetGuidAndStringKey";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IMyGrainWithGuidCompoundKey);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IMyGrainWithGuidCompoundKey)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            _target = default;
        }

        protected override global::System.Threading.Tasks.Task<global::System.Tuple<global::System.Guid, string>> InvokeInner() => _target.GetGuidAndStringKey();
        async global::System.Threading.Tasks.ValueTask<global::Orleans.Serialization.Invocation.Response> global::Orleans.Serialization.Invocation.IResponseInvokable.InvokeAndCopy(global::Orleans.Serialization.Serializers.ICodecProvider provider, global::Orleans.Serialization.Cloning.CopyContextPool contexts, global::Orleans.Serialization.DeepCopier<global::Orleans.Serialization.Invocation.Response> responseCopier)
        {
            try
            {
                var factory = global::OrleansCodeGen.TestProject.RpcResponse_6A3EE8F4Factory.Resolve(provider);
                if (!factory.IsSupported)
                    return responseCopier.Copy(await Invoke());
                global::System.Tuple<global::System.Guid, string> value = await InvokeInner();
                return factory.RentCopied(value, contexts);
            }
            catch (global::System.Exception exception)
            {
                return global::Orleans.Serialization.Invocation.Response.FromException(exception);
            }
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IMyGrainWithGuidCompoundKey : global::Orleans.Runtime.GrainReference, global::TestProject.IMyGrainWithGuidCompoundKey
    {
        public Proxy_IMyGrainWithGuidCompoundKey(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<global::System.Tuple<global::System.Guid, string>> global::TestProject.IMyGrainWithGuidCompoundKey.GetGuidAndStringKey()
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF();
            return base.InvokeAsync<global::System.Tuple<global::System.Guid, string>>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IMyGrainWithIntegerCompoundKey), "9814021A")]
    public sealed class Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A : global::Orleans.Runtime.TaskRequest<global::System.Tuple<long, string>>, global::Orleans.Serialization.Invocation.IResponseInvokable
    {
        global::TestProject.IMyGrainWithIntegerCompoundKey _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = typeof(global::TestProject.IMyGrainWithIntegerCompoundKey).GetMethod("GetIntegerAndStringKey", 0, global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance, null, global::System.Type.EmptyTypes, null);
        public override string GetMethodName() => "GetIntegerAndStringKey";
        public override string GetInterfaceName() => "TestProject.IMyGrainWithIntegerCompoundKey";
        public override string GetActivityName() => "IMyGrainWithIntegerCompoundKey/GetIntegerAndStringKey";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IMyGrainWithIntegerCompoundKey);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IMyGrainWithIntegerCompoundKey)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            _target = default;
        }

        protected override global::System.Threading.Tasks.Task<global::System.Tuple<long, string>> InvokeInner() => _target.GetIntegerAndStringKey();
        async global::System.Threading.Tasks.ValueTask<global::Orleans.Serialization.Invocation.Response> global::Orleans.Serialization.Invocation.IResponseInvokable.InvokeAndCopy(global::Orleans.Serialization.Serializers.ICodecProvider provider, global::Orleans.Serialization.Cloning.CopyContextPool contexts, global::Orleans.Serialization.DeepCopier<global::Orleans.Serialization.Invocation.Response> responseCopier)
        {
            try
            {
                var factory = global::OrleansCodeGen.TestProject.RpcResponse_AFB713E4Factory.Resolve(provider);
                if (!factory.IsSupported)
                    return responseCopier.Copy(await Invoke());
                global::System.Tuple<long, string> value = await InvokeInner();
                return factory.RentCopied(value, contexts);
            }
            catch (global::System.Exception exception)
            {
                return global::Orleans.Serialization.Invocation.Response.FromException(exception);
            }
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IMyGrainWithIntegerCompoundKey : global::Orleans.Runtime.GrainReference, global::TestProject.IMyGrainWithIntegerCompoundKey
    {
        public Proxy_IMyGrainWithIntegerCompoundKey(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<global::System.Tuple<long, string>> global::TestProject.IMyGrainWithIntegerCompoundKey.GetIntegerAndStringKey()
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A();
            return base.InvokeAsync<global::System.Tuple<long, string>>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E instance)
        {
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E @value)
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
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E DeepCopy(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E();
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_GrainWithGuidKey : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.GrainWithGuidKey>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.GrainWithGuidKey>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.GrainWithGuidKey);
        private readonly global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer;
        public Codec_GrainWithGuidKey(global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer)
        {
            this._baseTypeSerializer = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeSerializer);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.GrainWithGuidKey instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _baseTypeSerializer.Serialize(ref writer, instance);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.GrainWithGuidKey instance)
        {
            _baseTypeSerializer.Deserialize(ref reader, instance);
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.GrainWithGuidKey @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.GrainWithGuidKey))
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
        public global::TestProject.GrainWithGuidKey ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.GrainWithGuidKey, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.GrainWithGuidKey();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.GrainWithGuidKey>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_GrainWithGuidKey : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.GrainWithGuidKey>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.GrainWithGuidKey>
    {
        private readonly global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.GrainWithGuidKey DeepCopy(global::TestProject.GrainWithGuidKey original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.GrainWithGuidKey existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.GrainWithGuidKey))
                return context.DeepCopy(original);
            var result = new global::TestProject.GrainWithGuidKey();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_GrainWithGuidKey(global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier)
        {
            this._baseTypeCopier = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeCopier);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.GrainWithGuidKey input, global::TestProject.GrainWithGuidKey output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            _baseTypeCopier.DeepCopy(input, output, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_GrainWithGuidKey : global::Orleans.Serialization.Activators.IActivator<global::TestProject.GrainWithGuidKey>
    {
        public global::TestProject.GrainWithGuidKey Create() => new global::TestProject.GrainWithGuidKey();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IMyGrainWithStringKey_GrainReference_43570316 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316 instance)
        {
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IMyGrainWithStringKey_GrainReference_43570316 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316 DeepCopy(OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316();
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_GrainWithStringKey : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.GrainWithStringKey>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.GrainWithStringKey>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.GrainWithStringKey);
        private readonly global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer;
        public Codec_GrainWithStringKey(global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer)
        {
            this._baseTypeSerializer = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeSerializer);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.GrainWithStringKey instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _baseTypeSerializer.Serialize(ref writer, instance);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.GrainWithStringKey instance)
        {
            _baseTypeSerializer.Deserialize(ref reader, instance);
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.GrainWithStringKey @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.GrainWithStringKey))
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
        public global::TestProject.GrainWithStringKey ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.GrainWithStringKey, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.GrainWithStringKey();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.GrainWithStringKey>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_GrainWithStringKey : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.GrainWithStringKey>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.GrainWithStringKey>
    {
        private readonly global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.GrainWithStringKey DeepCopy(global::TestProject.GrainWithStringKey original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.GrainWithStringKey existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.GrainWithStringKey))
                return context.DeepCopy(original);
            var result = new global::TestProject.GrainWithStringKey();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_GrainWithStringKey(global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier)
        {
            this._baseTypeCopier = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeCopier);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.GrainWithStringKey input, global::TestProject.GrainWithStringKey output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            _baseTypeCopier.DeepCopy(input, output, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_GrainWithStringKey : global::Orleans.Serialization.Activators.IActivator<global::TestProject.GrainWithStringKey>
    {
        public global::TestProject.GrainWithStringKey Create() => new global::TestProject.GrainWithStringKey();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF instance)
        {
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF @value)
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
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF DeepCopy(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF();
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_GrainWithGuidCompoundKey : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.GrainWithGuidCompoundKey>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.GrainWithGuidCompoundKey>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.GrainWithGuidCompoundKey);
        private readonly global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer;
        public Codec_GrainWithGuidCompoundKey(global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer)
        {
            this._baseTypeSerializer = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeSerializer);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.GrainWithGuidCompoundKey instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _baseTypeSerializer.Serialize(ref writer, instance);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.GrainWithGuidCompoundKey instance)
        {
            _baseTypeSerializer.Deserialize(ref reader, instance);
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.GrainWithGuidCompoundKey @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.GrainWithGuidCompoundKey))
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
        public global::TestProject.GrainWithGuidCompoundKey ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.GrainWithGuidCompoundKey, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.GrainWithGuidCompoundKey();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.GrainWithGuidCompoundKey>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_GrainWithGuidCompoundKey : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.GrainWithGuidCompoundKey>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.GrainWithGuidCompoundKey>
    {
        private readonly global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.GrainWithGuidCompoundKey DeepCopy(global::TestProject.GrainWithGuidCompoundKey original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.GrainWithGuidCompoundKey existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.GrainWithGuidCompoundKey))
                return context.DeepCopy(original);
            var result = new global::TestProject.GrainWithGuidCompoundKey();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_GrainWithGuidCompoundKey(global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier)
        {
            this._baseTypeCopier = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeCopier);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.GrainWithGuidCompoundKey input, global::TestProject.GrainWithGuidCompoundKey output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            _baseTypeCopier.DeepCopy(input, output, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_GrainWithGuidCompoundKey : global::Orleans.Serialization.Activators.IActivator<global::TestProject.GrainWithGuidCompoundKey>
    {
        public global::TestProject.GrainWithGuidCompoundKey Create() => new global::TestProject.GrainWithGuidCompoundKey();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A instance)
        {
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A @value)
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
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A DeepCopy(OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A();
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_GrainWithIntegerCompoundKey : global::Orleans.Serialization.Codecs.IFieldCodec<global::TestProject.GrainWithIntegerCompoundKey>, global::Orleans.Serialization.Serializers.IBaseCodec<global::TestProject.GrainWithIntegerCompoundKey>
    {
        private readonly global::System.Type _codecFieldType = typeof(global::TestProject.GrainWithIntegerCompoundKey);
        private readonly global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer;
        public Codec_GrainWithIntegerCompoundKey(global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Grain> _baseTypeSerializer)
        {
            this._baseTypeSerializer = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeSerializer);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::TestProject.GrainWithIntegerCompoundKey instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            _baseTypeSerializer.Serialize(ref writer, instance);
            writer.WriteEndBase();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::TestProject.GrainWithIntegerCompoundKey instance)
        {
            _baseTypeSerializer.Deserialize(ref reader, instance);
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, global::TestProject.GrainWithIntegerCompoundKey @value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (@value is null || @value.GetType() == typeof(global::TestProject.GrainWithIntegerCompoundKey))
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
        public global::TestProject.GrainWithIntegerCompoundKey ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<global::TestProject.GrainWithIntegerCompoundKey, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            global::System.Type valueType = field.FieldType;
            if (valueType is null || valueType == _codecFieldType)
            {
                var result = new global::TestProject.GrainWithIntegerCompoundKey();
                ReferenceCodec.RecordObject(reader.Session, result);
                Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TReaderInput, global::TestProject.GrainWithIntegerCompoundKey>(ref field);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_GrainWithIntegerCompoundKey : global::Orleans.Serialization.Cloning.IDeepCopier<global::TestProject.GrainWithIntegerCompoundKey>, global::Orleans.Serialization.Cloning.IBaseCopier<global::TestProject.GrainWithIntegerCompoundKey>
    {
        private readonly global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public global::TestProject.GrainWithIntegerCompoundKey DeepCopy(global::TestProject.GrainWithIntegerCompoundKey original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (context.TryGetCopy(original, out global::TestProject.GrainWithIntegerCompoundKey existing))
                return existing;
            if (original.GetType() != typeof(global::TestProject.GrainWithIntegerCompoundKey))
                return context.DeepCopy(original);
            var result = new global::TestProject.GrainWithIntegerCompoundKey();
            context.RecordCopy(original, result);
            DeepCopy(original, result, context);
            return result;
        }

        public Copier_GrainWithIntegerCompoundKey(global::Orleans.Serialization.Cloning.IBaseCopier<global::Orleans.Grain> _baseTypeCopier)
        {
            this._baseTypeCopier = OrleansGeneratedCodeHelper.UnwrapService(this, _baseTypeCopier);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void DeepCopy(global::TestProject.GrainWithIntegerCompoundKey input, global::TestProject.GrainWithIntegerCompoundKey output, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            _baseTypeCopier.DeepCopy(input, output, context);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_GrainWithIntegerCompoundKey : global::Orleans.Serialization.Activators.IActivator<global::TestProject.GrainWithIntegerCompoundKey>
    {
        public global::TestProject.GrainWithIntegerCompoundKey Create() => new global::TestProject.GrainWithIntegerCompoundKey();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class RpcResponseFactories : global::Orleans.Serialization.SerializerContext
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions options)
        {
            options.AddDefaultSerializerService<RpcResponse_5C3A711CFactory>(static provider => new RpcResponse_5C3A711CFactory(provider));
            options.AddDefaultSerializer<RpcResponse_5C3A711C>(static provider => RpcResponse_5C3A711CFactory.Resolve(provider), static provider => RpcResponse_5C3A711CFactory.Resolve(provider));
            options.AddRawResponseReader<global::System.Guid>(static provider => RpcResponse_5C3A711CFactory.Resolve(provider));
            options.AddDefaultSerializerService<RpcResponse_6A3EE8F4Factory>(static provider => new RpcResponse_6A3EE8F4Factory(provider));
            options.AddDefaultSerializer<RpcResponse_6A3EE8F4>(static provider => RpcResponse_6A3EE8F4Factory.Resolve(provider), static provider => RpcResponse_6A3EE8F4Factory.Resolve(provider));
            options.AddRawResponseReader<global::System.Tuple<global::System.Guid, string>>(static provider => RpcResponse_6A3EE8F4Factory.Resolve(provider));
            options.AddDefaultSerializerService<RpcResponse_AFB713E4Factory>(static provider => new RpcResponse_AFB713E4Factory(provider));
            options.AddDefaultSerializer<RpcResponse_AFB713E4>(static provider => RpcResponse_AFB713E4Factory.Resolve(provider), static provider => RpcResponse_AFB713E4Factory.Resolve(provider));
            options.AddRawResponseReader<global::System.Tuple<long, string>>(static provider => RpcResponse_AFB713E4Factory.Resolve(provider));
            options.AddDefaultSerializerService<RpcResponse_9146C7E3Factory>(static provider => new RpcResponse_9146C7E3Factory(provider));
            options.AddDefaultSerializer<RpcResponse_9146C7E3>(static provider => RpcResponse_9146C7E3Factory.Resolve(provider), static provider => RpcResponse_9146C7E3Factory.Resolve(provider));
            options.AddRawResponseReader<string>(static provider => RpcResponse_9146C7E3Factory.Resolve(provider));
#if NET5_0_OR_GREATER
            if (global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                return;
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Guid, global::Orleans.Serialization.Codecs.GuidCodec>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Guid, global::Orleans.Serialization.Codecs.GuidCodec>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.GuidCodec>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Guid, global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Guid, global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>>(null !, provider)));
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<global::System.Guid>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Guid, global::Orleans.Serialization.Codecs.GuidCodec>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Guid, global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>>>(null !, provider));
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.Response<global::System.Guid>));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>>(null !, provider)));
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<global::System.Guid, string>>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>>>(null !, provider));
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.Response<global::System.Tuple<global::System.Guid, string>>));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCodec<long, string>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCodec<long, string>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCodec<long, string>>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCopier<long, string>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCopier<long, string>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCopier<long, string>>(null !, provider)));
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<long, string>>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCodec<long, string>>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCopier<long, string>>>(null !, provider));
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.Response<global::System.Tuple<long, string>>));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.StringCodec>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>>(static provider => new global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(null !, provider)));
            options.AddDefaultSerializer<global::Orleans.Serialization.Invocation.Response<string>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCodec<string, global::Orleans.Serialization.Codecs.StringCodec>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Invocation.PooledResponseCopier<string, global::Orleans.Serialization.Cloning.ShallowCopier<string>>>(null !, provider));
            options.AddAllowedType(typeof(global::Orleans.Serialization.Invocation.Response<string>));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.GuidCodec>(static provider => new global::Orleans.Serialization.Codecs.GuidCodec());
            options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>>(static provider => new global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>());
            options.AddDefaultSerializer<global::System.Guid>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.GuidCodec>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>>(null !, provider));
            options.AddAllowedType(typeof(global::System.Guid));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>>(static provider => new global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.GuidCodec>(null !, provider), global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.StringCodec>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>>(static provider => new global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>>(null !, provider), global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(null !, provider)));
            options.AddDefaultSerializer<global::System.Tuple<global::System.Guid, string>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>>(null !, provider));
            options.AddAllowedType(typeof(global::System.Tuple<global::System.Guid, string>));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.TupleCodec<long, string>>(static provider => new global::Orleans.Serialization.Codecs.TupleCodec<long, string>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.Int64Codec>(null !, provider), global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.StringCodec>(null !, provider)));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.TupleCopier<long, string>>(static provider => new global::Orleans.Serialization.Codecs.TupleCopier<long, string>(global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<long>>(null !, provider), global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<string>>(null !, provider)));
            options.AddDefaultSerializer<global::System.Tuple<long, string>>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCodec<long, string>>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.TupleCopier<long, string>>(null !, provider));
            options.AddAllowedType(typeof(global::System.Tuple<long, string>));
            options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.Int64Codec>(static provider => new global::Orleans.Serialization.Codecs.Int64Codec());
            options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.ShallowCopier<long>>(static provider => new global::Orleans.Serialization.Cloning.ShallowCopier<long>());
            options.AddDefaultSerializer<long>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Codecs.Int64Codec>(null !, provider), static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<global::Orleans.Serialization.Cloning.ShallowCopier<long>>(null !, provider));
            options.AddAllowedType(typeof(long));
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

    internal sealed class RpcResponse_5C3A711C : global::Orleans.Serialization.Invocation.Response, global::Orleans.Serialization.Invocation.IRawResponseWriter
    {
        internal global::System.Guid Value;
        private RpcResponse_5C3A711CFactory _factory;
        public RpcResponse_5C3A711C()
        {
        }

        internal static RpcResponse_5C3A711C Rent(global::System.Guid value, RpcResponse_5C3A711CFactory factory)
        {
            var result = global::Orleans.Serialization.Invocation.ResponsePool.GetGenerated<RpcResponse_5C3A711C>();
            result.Value = value;
            result._factory = factory;
            return result;
        }

        public override object Result { get => Value; set => Value = (global::System.Guid)value; }
        public override global::System.Exception Exception { get => null; set => throw new global::System.InvalidOperationException("Successful response holders contain result values."); }

        public override global::System.Type GetSimpleResultType() => typeof(global::System.Guid);
        public override T GetResult<T>()
        {
            if (typeof(T) == typeof(global::System.Guid))
                return global::System.Runtime.CompilerServices.Unsafe.As<global::System.Guid, T>(ref Value);
            return (T)(object)Value;
        }

        public void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (_factory is null)
                throw new global::System.ObjectDisposedException(GetType().Name);
            writer.WriteStartObject(0, null, typeof(global::System.Guid));
            global::Orleans.Serialization.Codecs.GuidCodec.WriteField(ref writer, 0, Value);
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

    internal sealed class RpcResponse_5C3A711CFactory : global::Orleans.Serialization.Invocation.ResponseCodec, global::Orleans.Serialization.Codecs.IFieldCodec<RpcResponse_5C3A711C>, global::Orleans.Serialization.Cloning.IDeepCopier<RpcResponse_5C3A711C>, global::Orleans.Serialization.Invocation.IRawResponseReader
    {
        private readonly global::Orleans.Serialization.Codecs.GuidCodec _codec;
        private readonly global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid> _copier;
        public bool IsSupported { get; }

        internal static RpcResponse_5C3A711CFactory Resolve(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            provider.GetCodec<global::System.Guid>();
            provider.GetDeepCopier<global::System.Guid>();
            provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::System.Guid>>();
            provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::System.Guid>>();
            return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<RpcResponse_5C3A711CFactory>(null, provider);
        }

        public RpcResponse_5C3A711CFactory(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            _codec = provider.GetCodec<global::System.Guid>() as global::Orleans.Serialization.Codecs.GuidCodec;
            _copier = provider.GetDeepCopier<global::System.Guid>() as global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>;
            var responseCodec = provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::System.Guid>>();
            var responseCopier = provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::System.Guid>>();
            IsSupported = _codec is not null && _copier is not null && (responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Guid, global::Orleans.Serialization.Codecs.GuidCodec> || responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Guid, global::Orleans.Serialization.Codecs.IFieldCodec<global::System.Guid>>) && (responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Guid, global::Orleans.Serialization.Cloning.ShallowCopier<global::System.Guid>> || responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Guid, global::Orleans.Serialization.Cloning.IDeepCopier<global::System.Guid>>);
        }

        internal global::Orleans.Serialization.Invocation.Response RentCopied(global::System.Guid value, global::Orleans.Serialization.Cloning.CopyContextPool contexts)
        {
            return RpcResponse_5C3A711C.Rent(value, this);
        }

        [return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull("input")]
        public RpcResponse_5C3A711C DeepCopy(RpcResponse_5C3A711C input, global::Orleans.Serialization.Cloning.CopyContext context) => input is null ? null : RpcResponse_5C3A711C.Rent(input.Value, this);
        internal void WriteResult<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::System.Guid value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteStartObject(0, null, typeof(global::System.Guid));
            global::Orleans.Serialization.Codecs.GuidCodec.WriteField(ref writer, 0, value);
            writer.WriteEndObject();
        }

        public override void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, object value) => WriteResult(ref writer, ((RpcResponse_5C3A711C)value).Value);
        public override object ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        global::Orleans.Serialization.Invocation.Response global::Orleans.Serialization.Invocation.IRawResponseReader.ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        private RpcResponse_5C3A711C ReadResult<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = RpcResponse_5C3A711C.Rent(default, this);
            try
            {
                reader.ReadFieldHeader(ref field);
                if (!field.IsEndBaseOrEndObject)
                {
                    result.Value = global::Orleans.Serialization.Codecs.GuidCodec.ReadValue(ref reader, field);
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

        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, RpcResponse_5C3A711C value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (value is null)
            {
                global::Orleans.Serialization.Codecs.ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(global::Orleans.Serialization.Invocation.Response<global::System.Guid>));
            global::Orleans.Serialization.Codecs.GuidCodec.WriteField(ref writer, 0, value.Value);
            writer.WriteEndObject();
        }

        public RpcResponse_5C3A711C ReadValue<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<RpcResponse_5C3A711C, TInput>(ref reader, field);
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            return ReadResult(ref reader, ref field);
        }
    }

    internal sealed class RpcResponse_6A3EE8F4 : global::Orleans.Serialization.Invocation.Response, global::Orleans.Serialization.Invocation.IRawResponseWriter
    {
        internal global::System.Tuple<global::System.Guid, string> Value;
        private RpcResponse_6A3EE8F4Factory _factory;
        public RpcResponse_6A3EE8F4()
        {
        }

        internal static RpcResponse_6A3EE8F4 Rent(global::System.Tuple<global::System.Guid, string> value, RpcResponse_6A3EE8F4Factory factory)
        {
            var result = global::Orleans.Serialization.Invocation.ResponsePool.GetGenerated<RpcResponse_6A3EE8F4>();
            result.Value = value;
            result._factory = factory;
            return result;
        }

        public override object Result { get => Value; set => Value = (global::System.Tuple<global::System.Guid, string>)value; }
        public override global::System.Exception Exception { get => null; set => throw new global::System.InvalidOperationException("Successful response holders contain result values."); }

        public override global::System.Type GetSimpleResultType() => typeof(global::System.Tuple<global::System.Guid, string>);
        public override T GetResult<T>()
        {
            if (typeof(T) == typeof(global::System.Tuple<global::System.Guid, string>))
                return global::System.Runtime.CompilerServices.Unsafe.As<global::System.Tuple<global::System.Guid, string>, T>(ref Value);
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

    internal sealed class RpcResponse_6A3EE8F4Factory : global::Orleans.Serialization.Invocation.ResponseCodec, global::Orleans.Serialization.Codecs.IFieldCodec<RpcResponse_6A3EE8F4>, global::Orleans.Serialization.Cloning.IDeepCopier<RpcResponse_6A3EE8F4>, global::Orleans.Serialization.Invocation.IRawResponseReader
    {
        private readonly global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string> _codec;
        private readonly global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string> _copier;
        public bool IsSupported { get; }

        internal static RpcResponse_6A3EE8F4Factory Resolve(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            provider.GetCodec<global::System.Tuple<global::System.Guid, string>>();
            provider.GetDeepCopier<global::System.Tuple<global::System.Guid, string>>();
            provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<global::System.Guid, string>>>();
            provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<global::System.Guid, string>>>();
            return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<RpcResponse_6A3EE8F4Factory>(null, provider);
        }

        public RpcResponse_6A3EE8F4Factory(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            _codec = provider.GetCodec<global::System.Tuple<global::System.Guid, string>>() as global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>;
            _copier = provider.GetDeepCopier<global::System.Tuple<global::System.Guid, string>>() as global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>;
            var responseCodec = provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<global::System.Guid, string>>>();
            var responseCopier = provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<global::System.Guid, string>>>();
            IsSupported = _codec is not null && _copier is not null && (responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCodec<global::System.Guid, string>> || responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.IFieldCodec<global::System.Tuple<global::System.Guid, string>>>) && (responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Codecs.TupleCopier<global::System.Guid, string>> || responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<global::System.Guid, string>, global::Orleans.Serialization.Cloning.IDeepCopier<global::System.Tuple<global::System.Guid, string>>>);
        }

        internal global::Orleans.Serialization.Invocation.Response RentCopied(global::System.Tuple<global::System.Guid, string> value, global::Orleans.Serialization.Cloning.CopyContextPool contexts)
        {
            return RpcResponse_6A3EE8F4.Rent(value, this);
        }

        [return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull("input")]
        public RpcResponse_6A3EE8F4 DeepCopy(RpcResponse_6A3EE8F4 input, global::Orleans.Serialization.Cloning.CopyContext context) => input is null ? null : RpcResponse_6A3EE8F4.Rent(input.Value, this);
        internal void WriteResult<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::System.Tuple<global::System.Guid, string> value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteStartObject(0, null, typeof(global::System.Tuple<global::System.Guid, string>));
            if (value is not null)
            {
                _codec.WriteField(ref writer, 0, typeof(global::System.Tuple<global::System.Guid, string>), value);
            }

            writer.WriteEndObject();
        }

        public override void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, object value) => WriteResult(ref writer, ((RpcResponse_6A3EE8F4)value).Value);
        public override object ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        global::Orleans.Serialization.Invocation.Response global::Orleans.Serialization.Invocation.IRawResponseReader.ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        private RpcResponse_6A3EE8F4 ReadResult<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = RpcResponse_6A3EE8F4.Rent(default, this);
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

        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, RpcResponse_6A3EE8F4 value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (value is null)
            {
                global::Orleans.Serialization.Codecs.ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(global::Orleans.Serialization.Invocation.Response<global::System.Tuple<global::System.Guid, string>>));
            if (value.Value is not null)
            {
                _codec.WriteField(ref writer, 0, typeof(global::System.Tuple<global::System.Guid, string>), value.Value);
            }

            writer.WriteEndObject();
        }

        public RpcResponse_6A3EE8F4 ReadValue<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<RpcResponse_6A3EE8F4, TInput>(ref reader, field);
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            return ReadResult(ref reader, ref field);
        }
    }

    internal sealed class RpcResponse_AFB713E4 : global::Orleans.Serialization.Invocation.Response, global::Orleans.Serialization.Invocation.IRawResponseWriter
    {
        internal global::System.Tuple<long, string> Value;
        private RpcResponse_AFB713E4Factory _factory;
        public RpcResponse_AFB713E4()
        {
        }

        internal static RpcResponse_AFB713E4 Rent(global::System.Tuple<long, string> value, RpcResponse_AFB713E4Factory factory)
        {
            var result = global::Orleans.Serialization.Invocation.ResponsePool.GetGenerated<RpcResponse_AFB713E4>();
            result.Value = value;
            result._factory = factory;
            return result;
        }

        public override object Result { get => Value; set => Value = (global::System.Tuple<long, string>)value; }
        public override global::System.Exception Exception { get => null; set => throw new global::System.InvalidOperationException("Successful response holders contain result values."); }

        public override global::System.Type GetSimpleResultType() => typeof(global::System.Tuple<long, string>);
        public override T GetResult<T>()
        {
            if (typeof(T) == typeof(global::System.Tuple<long, string>))
                return global::System.Runtime.CompilerServices.Unsafe.As<global::System.Tuple<long, string>, T>(ref Value);
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

    internal sealed class RpcResponse_AFB713E4Factory : global::Orleans.Serialization.Invocation.ResponseCodec, global::Orleans.Serialization.Codecs.IFieldCodec<RpcResponse_AFB713E4>, global::Orleans.Serialization.Cloning.IDeepCopier<RpcResponse_AFB713E4>, global::Orleans.Serialization.Invocation.IRawResponseReader
    {
        private readonly global::Orleans.Serialization.Codecs.TupleCodec<long, string> _codec;
        private readonly global::Orleans.Serialization.Codecs.TupleCopier<long, string> _copier;
        public bool IsSupported { get; }

        internal static RpcResponse_AFB713E4Factory Resolve(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            provider.GetCodec<global::System.Tuple<long, string>>();
            provider.GetDeepCopier<global::System.Tuple<long, string>>();
            provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<long, string>>>();
            provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<long, string>>>();
            return global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<RpcResponse_AFB713E4Factory>(null, provider);
        }

        public RpcResponse_AFB713E4Factory(global::Orleans.Serialization.Serializers.ICodecProvider provider)
        {
            _codec = provider.GetCodec<global::System.Tuple<long, string>>() as global::Orleans.Serialization.Codecs.TupleCodec<long, string>;
            _copier = provider.GetDeepCopier<global::System.Tuple<long, string>>() as global::Orleans.Serialization.Codecs.TupleCopier<long, string>;
            var responseCodec = provider.GetCodec<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<long, string>>>();
            var responseCopier = provider.GetDeepCopier<global::Orleans.Serialization.Invocation.Response<global::System.Tuple<long, string>>>();
            IsSupported = _codec is not null && _copier is not null && (responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCodec<long, string>> || responseCodec is global::Orleans.Serialization.Invocation.PooledResponseCodec<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.IFieldCodec<global::System.Tuple<long, string>>>) && (responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<long, string>, global::Orleans.Serialization.Codecs.TupleCopier<long, string>> || responseCopier is global::Orleans.Serialization.Invocation.PooledResponseCopier<global::System.Tuple<long, string>, global::Orleans.Serialization.Cloning.IDeepCopier<global::System.Tuple<long, string>>>);
        }

        internal global::Orleans.Serialization.Invocation.Response RentCopied(global::System.Tuple<long, string> value, global::Orleans.Serialization.Cloning.CopyContextPool contexts)
        {
            return RpcResponse_AFB713E4.Rent(value, this);
        }

        [return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull("input")]
        public RpcResponse_AFB713E4 DeepCopy(RpcResponse_AFB713E4 input, global::Orleans.Serialization.Cloning.CopyContext context) => input is null ? null : RpcResponse_AFB713E4.Rent(input.Value, this);
        internal void WriteResult<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::System.Tuple<long, string> value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            writer.WriteStartObject(0, null, typeof(global::System.Tuple<long, string>));
            if (value is not null)
            {
                _codec.WriteField(ref writer, 0, typeof(global::System.Tuple<long, string>), value);
            }

            writer.WriteEndObject();
        }

        public override void WriteRaw<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, object value) => WriteResult(ref writer, ((RpcResponse_AFB713E4)value).Value);
        public override object ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        global::Orleans.Serialization.Invocation.Response global::Orleans.Serialization.Invocation.IRawResponseReader.ReadRaw<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field) => ReadResult(ref reader, ref field);
        private RpcResponse_AFB713E4 ReadResult<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, scoped ref global::Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = RpcResponse_AFB713E4.Rent(default, this);
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

        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, RpcResponse_AFB713E4 value)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            if (value is null)
            {
                global::Orleans.Serialization.Codecs.ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, typeof(global::Orleans.Serialization.Invocation.Response<global::System.Tuple<long, string>>));
            if (value.Value is not null)
            {
                _codec.WriteField(ref writer, 0, typeof(global::System.Tuple<long, string>), value.Value);
            }

            writer.WriteEndObject();
        }

        public RpcResponse_AFB713E4 ReadValue<TInput>(ref global::Orleans.Serialization.Buffers.Reader<TInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return global::Orleans.Serialization.Codecs.ReferenceCodec.ReadReference<RpcResponse_AFB713E4, TInput>(ref reader, field);
            global::Orleans.Serialization.Codecs.ReferenceCodec.MarkValueField(reader.Session);
            return ReadResult(ref reader, ref field);
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
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_GrainWithGuidKey), typeof(global::TestProject.GrainWithGuidKey));
            config.AddBaseCodec(typeof(OrleansCodeGen.TestProject.Codec_GrainWithGuidKey), typeof(global::TestProject.GrainWithGuidKey));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IMyGrainWithStringKey_GrainReference_43570316), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_GrainWithStringKey), typeof(global::TestProject.GrainWithStringKey));
            config.AddBaseCodec(typeof(OrleansCodeGen.TestProject.Codec_GrainWithStringKey), typeof(global::TestProject.GrainWithStringKey));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_GrainWithGuidCompoundKey), typeof(global::TestProject.GrainWithGuidCompoundKey));
            config.AddBaseCodec(typeof(OrleansCodeGen.TestProject.Codec_GrainWithGuidCompoundKey), typeof(global::TestProject.GrainWithGuidCompoundKey));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_GrainWithIntegerCompoundKey), typeof(global::TestProject.GrainWithIntegerCompoundKey));
            config.AddBaseCodec(typeof(OrleansCodeGen.TestProject.Codec_GrainWithIntegerCompoundKey), typeof(global::TestProject.GrainWithIntegerCompoundKey));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithGuidKey), typeof(global::TestProject.GrainWithGuidKey));
            config.AddBaseCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithGuidKey), typeof(global::TestProject.GrainWithGuidKey));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IMyGrainWithStringKey_GrainReference_43570316), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithStringKey), typeof(global::TestProject.GrainWithStringKey));
            config.AddBaseCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithStringKey), typeof(global::TestProject.GrainWithStringKey));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithGuidCompoundKey), typeof(global::TestProject.GrainWithGuidCompoundKey));
            config.AddBaseCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithGuidCompoundKey), typeof(global::TestProject.GrainWithGuidCompoundKey));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A), typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithIntegerCompoundKey), typeof(global::TestProject.GrainWithIntegerCompoundKey));
            config.AddBaseCopier(typeof(OrleansCodeGen.TestProject.Copier_GrainWithIntegerCompoundKey), typeof(global::TestProject.GrainWithIntegerCompoundKey));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IMyGrainWithGuidKey));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IMyGrainWithStringKey));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IMyGrainWithGuidCompoundKey));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IMyGrainWithIntegerCompoundKey));
            config.AddInterface(typeof(global::TestProject.IMyGrainWithGuidKey));
            config.AddInterface(typeof(global::TestProject.IMyGrainWithStringKey));
            config.AddInterface(typeof(global::TestProject.IMyGrainWithGuidCompoundKey));
            config.AddInterface(typeof(global::TestProject.IMyGrainWithIntegerCompoundKey));
            config.AddInterfaceImplementation(typeof(global::TestProject.GrainWithGuidKey));
            config.AddInterfaceImplementation(typeof(global::TestProject.GrainWithStringKey));
            config.AddInterfaceImplementation(typeof(global::TestProject.GrainWithGuidCompoundKey));
            config.AddInterfaceImplementation(typeof(global::TestProject.GrainWithIntegerCompoundKey));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_GrainWithGuidKey), typeof(global::TestProject.GrainWithGuidKey));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_GrainWithStringKey), typeof(global::TestProject.GrainWithStringKey));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_GrainWithGuidCompoundKey), typeof(global::TestProject.GrainWithGuidCompoundKey));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_GrainWithIntegerCompoundKey), typeof(global::TestProject.GrainWithIntegerCompoundKey));
            var n1 = config.CompoundTypeAliases.GetOrAdd("inv");
            var n2 = n1.GetOrAdd(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.GetOrAdd(typeof(global::TestProject.IMyGrainWithGuidKey));
            n3.Add("8F0FEC0E", typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidKey_GrainReference_8F0FEC0E));
            var n5 = n2.GetOrAdd(typeof(global::TestProject.IMyGrainWithStringKey));
            n5.Add("43570316", typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithStringKey_GrainReference_43570316));
            var n7 = n2.GetOrAdd(typeof(global::TestProject.IMyGrainWithGuidCompoundKey));
            n7.Add("A9FEF7AF", typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithGuidCompoundKey_GrainReference_A9FEF7AF));
            var n9 = n2.GetOrAdd(typeof(global::TestProject.IMyGrainWithIntegerCompoundKey));
            n9.Add("9814021A", typeof(OrleansCodeGen.TestProject.Invokable_IMyGrainWithIntegerCompoundKey_GrainReference_9814021A));
        }
    }
}
#pragma warning restore