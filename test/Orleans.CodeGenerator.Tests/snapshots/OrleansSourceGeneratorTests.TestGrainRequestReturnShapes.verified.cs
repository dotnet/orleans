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
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IRequestShapeGrain), "179DFF79")]
    public sealed class Invokable_IRequestShapeGrain_GrainReference_179DFF79 : global::Orleans.Runtime.TaskRequest<int>
    {
        public byte[] arg0;
        global::TestProject.IRequestShapeGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IRequestShapeGrain), "TaskOfTMethod", null, new[] { typeof(byte[]) });
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_179DFF79> _pool;
        public Invokable_IRequestShapeGrain_GrainReference_179DFF79() : this(null !)
        {
        }

        public Invokable_IRequestShapeGrain_GrainReference_179DFF79(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_179DFF79> pool) : base()
        {
            _pool = pool;
        }

        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "TaskOfTMethod";
        public override string GetInterfaceName() => "TestProject.IRequestShapeGrain";
        public override string GetActivityName() => "IRequestShapeGrain/TaskOfTMethod";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IRequestShapeGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IRequestShapeGrain)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            arg0 = default;
            _target = default;
            Options = default;
            _pool?.Return(this);
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
                    arg0 = (byte[])value;
                    return;
                default:
                    OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 0);
                    return;
            }
        }

        protected override global::System.Threading.Tasks.Task<int> InvokeInner() => _target.TaskOfTMethod(arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IRequestShapeGrain), "3637890A")]
    public sealed class Invokable_IRequestShapeGrain_GrainReference_3637890A : global::Orleans.Runtime.Request
    {
        public int arg0;
        global::TestProject.IRequestShapeGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IRequestShapeGrain), "ValueTaskMethod", null, new[] { typeof(int) });
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_3637890A> _pool;
        public Invokable_IRequestShapeGrain_GrainReference_3637890A() : this(null !)
        {
        }

        public Invokable_IRequestShapeGrain_GrainReference_3637890A(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_3637890A> pool) : base()
        {
            _pool = pool;
        }

        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "ValueTaskMethod";
        public override string GetInterfaceName() => "TestProject.IRequestShapeGrain";
        public override string GetActivityName() => "IRequestShapeGrain/ValueTaskMethod";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IRequestShapeGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IRequestShapeGrain)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            arg0 = default;
            _target = default;
            Options = default;
            _pool?.Return(this);
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
                    arg0 = (int)value;
                    return;
                default:
                    OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 0);
                    return;
            }
        }

        protected override global::System.Threading.Tasks.ValueTask InvokeInner() => _target.ValueTaskMethod(arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IRequestShapeGrain), "547E1673")]
    public sealed class Invokable_IRequestShapeGrain_GrainReference_547E1673 : global::Orleans.Runtime.TaskRequest
    {
        public object arg0;
        global::TestProject.IRequestShapeGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IRequestShapeGrain), "TaskMethod", null, new[] { typeof(object) });
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_547E1673> _pool;
        public Invokable_IRequestShapeGrain_GrainReference_547E1673() : this(null !)
        {
        }

        public Invokable_IRequestShapeGrain_GrainReference_547E1673(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_547E1673> pool) : base()
        {
            _pool = pool;
        }

        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "TaskMethod";
        public override string GetInterfaceName() => "TestProject.IRequestShapeGrain";
        public override string GetActivityName() => "IRequestShapeGrain/TaskMethod";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IRequestShapeGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IRequestShapeGrain)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            arg0 = default;
            _target = default;
            Options = default;
            _pool?.Return(this);
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
                    arg0 = (object)value;
                    return;
                default:
                    OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 0);
                    return;
            }
        }

        protected override global::System.Threading.Tasks.Task InvokeInner() => _target.TaskMethod(arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IRequestShapeGrain), "7597183B")]
    public sealed class Invokable_IRequestShapeGrain_GrainReference_7597183B : global::Orleans.Runtime.VoidRequest
    {
        public long arg0;
        global::TestProject.IRequestShapeGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IRequestShapeGrain), "OneWayMethod", null, new[] { typeof(long) });
        public Invokable_IRequestShapeGrain_GrainReference_7597183B() : base()
        {
            AddInvokeMethodOptions(global::Orleans.CodeGeneration.InvokeMethodOptions.OneWay);
        }

        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "OneWayMethod";
        public override string GetInterfaceName() => "TestProject.IRequestShapeGrain";
        public override string GetActivityName() => "IRequestShapeGrain/OneWayMethod";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IRequestShapeGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IRequestShapeGrain)holder.GetTarget();
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
                    arg0 = (long)value;
                    return;
                default:
                    OrleansGeneratedCodeHelper.InvokableThrowArgumentOutOfRange(index, 0);
                    return;
            }
        }

        protected override void InvokeInner() => _target.OneWayMethod(arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IRequestShapeGrain), "D2239DF3")]
    public sealed class Invokable_IRequestShapeGrain_GrainReference_D2239DF3 : global::Orleans.Runtime.Request<int>
    {
        public string arg0;
        global::TestProject.IRequestShapeGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IRequestShapeGrain), "ValueTaskOfTMethod", null, new[] { typeof(string) });
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_D2239DF3> _pool;
        public Invokable_IRequestShapeGrain_GrainReference_D2239DF3() : this(null !)
        {
        }

        public Invokable_IRequestShapeGrain_GrainReference_D2239DF3(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_D2239DF3> pool) : base()
        {
            _pool = pool;
        }

        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "ValueTaskOfTMethod";
        public override string GetInterfaceName() => "TestProject.IRequestShapeGrain";
        public override string GetActivityName() => "IRequestShapeGrain/ValueTaskOfTMethod";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IRequestShapeGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IRequestShapeGrain)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            arg0 = default;
            _target = default;
            Options = default;
            _pool?.Return(this);
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

        protected override global::System.Threading.Tasks.ValueTask<int> InvokeInner() => _target.ValueTaskOfTMethod(arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IRequestShapeGrain), "EB0D9BAC")]
    public sealed class Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC : global::Orleans.Runtime.TaskRequest
    {
        global::TestProject.IRequestShapeGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IRequestShapeGrain), "ParameterlessTaskMethod", null, null);
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> _pool;
        public Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC() : this(null !)
        {
        }

        public Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> pool) : base()
        {
            _pool = pool;
        }

        public override string GetMethodName() => "ParameterlessTaskMethod";
        public override string GetInterfaceName() => "TestProject.IRequestShapeGrain";
        public override string GetActivityName() => "IRequestShapeGrain/ParameterlessTaskMethod";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IRequestShapeGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = (global::TestProject.IRequestShapeGrain)holder.GetTarget();
        public override object GetTarget() => _target;
        public override void Dispose()
        {
            _target = default;
            Options = default;
            _pool?.Return(this);
        }

        protected override global::System.Threading.Tasks.Task InvokeInner() => _target.ParameterlessTaskMethod();
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IRequestShapeGrain : global::Orleans.Runtime.GrainReference, global::TestProject.IRequestShapeGrain
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> _activator_EB0D9BAC_8F1AD4FF;
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A> _activator_3637890A_7665EB95;
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3> _activator_D2239DF3_FE83C2A0;
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673> _activator_547E1673_CB2F447C;
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79> _activator_179DFF79_C44C095E;
        public Proxy_IRequestShapeGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
            _activator_EB0D9BAC_8F1AD4FF = CodecProvider.GetActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC>();
            _activator_3637890A_7665EB95 = CodecProvider.GetActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A>();
            _activator_D2239DF3_FE83C2A0 = CodecProvider.GetActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3>();
            _activator_547E1673_CB2F447C = CodecProvider.GetActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673>();
            _activator_179DFF79_C44C095E = CodecProvider.GetActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79>();
        }

        global::System.Threading.Tasks.Task global::TestProject.IRequestShapeGrain.ParameterlessTaskMethod()
        {
            var request = _activator_EB0D9BAC_8F1AD4FF.Create();
            return base.InvokeAsync(request).AsTask();
        }

        global::System.Threading.Tasks.ValueTask global::TestProject.IRequestShapeGrain.ValueTaskMethod(int arg0)
        {
            var request = _activator_3637890A_7665EB95.Create();
            request.arg0 = arg0;
            return base.InvokeAsync(request);
        }

        global::System.Threading.Tasks.ValueTask<int> global::TestProject.IRequestShapeGrain.ValueTaskOfTMethod(string arg0)
        {
            var request = _activator_D2239DF3_FE83C2A0.Create();
            request.arg0 = arg0;
            return base.InvokeAsync<int>(request);
        }

        global::System.Threading.Tasks.Task global::TestProject.IRequestShapeGrain.TaskMethod(object arg0)
        {
            var request = _activator_547E1673_CB2F447C.Create();
            using var copyContext = base.CopyContextPool.GetContext();
            request.arg0 = global::Orleans.Serialization.Codecs.ObjectCopier.DeepCopy(arg0, copyContext);
            return base.InvokeAsync(request).AsTask();
        }

        global::System.Threading.Tasks.Task<int> global::TestProject.IRequestShapeGrain.TaskOfTMethod(byte[] arg0)
        {
            var request = _activator_179DFF79_C44C095E.Create();
            using var copyContext = base.CopyContextPool.GetContext();
            request.arg0 = global::Orleans.Serialization.Codecs.ByteArrayCopier.DeepCopy(arg0, copyContext);
            return base.InvokeAsync<int>(request).AsTask();
        }

        void global::TestProject.IRequestShapeGrain.OneWayMethod(long arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B();
            request.arg0 = arg0;
            base.Invoke(request);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_179DFF79 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79);
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79> _activator;
        public Codec_Invokable_IRequestShapeGrain_GrainReference_179DFF79(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.ByteArrayCodec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 instance)
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
                    instance.arg0 = global::Orleans.Serialization.Codecs.ByteArrayCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = _activator.Create();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_179DFF79 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79> _activator;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = _activator.Create();
            result.arg0 = global::Orleans.Serialization.Codecs.ByteArrayCopier.DeepCopy(original.arg0, context);
            return result;
        }

        public Copier_Invokable_IRequestShapeGrain_GrainReference_179DFF79(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_Invokable_IRequestShapeGrain_GrainReference_179DFF79 : global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79>
    {
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_179DFF79> _arg0;
        public Activator_Invokable_IRequestShapeGrain_GrainReference_179DFF79(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_179DFF79> arg0)
        {
            _arg0 = arg0;
        }

        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 Create() => _arg0.TryGet(out var item) ? item : new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79(_arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_3637890A : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A);
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A> _activator;
        public Codec_Invokable_IRequestShapeGrain_GrainReference_3637890A(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int32Codec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A instance)
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
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A @value)
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
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = _activator.Create();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_3637890A : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A> _activator;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = _activator.Create();
            result.arg0 = original.arg0;
            return result;
        }

        public Copier_Invokable_IRequestShapeGrain_GrainReference_3637890A(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_Invokable_IRequestShapeGrain_GrainReference_3637890A : global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A>
    {
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_3637890A> _arg0;
        public Activator_Invokable_IRequestShapeGrain_GrainReference_3637890A(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_3637890A> arg0)
        {
            _arg0 = arg0;
        }

        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A Create() => _arg0.TryGet(out var item) ? item : new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A(_arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_547E1673 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673);
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673> _activator;
        public Codec_Invokable_IRequestShapeGrain_GrainReference_547E1673(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.ObjectCodec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 instance)
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
                    instance.arg0 = global::Orleans.Serialization.Codecs.ObjectCodec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = _activator.Create();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_547E1673 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673> _activator;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = _activator.Create();
            result.arg0 = global::Orleans.Serialization.Codecs.ObjectCopier.DeepCopy(original.arg0, context);
            return result;
        }

        public Copier_Invokable_IRequestShapeGrain_GrainReference_547E1673(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_Invokable_IRequestShapeGrain_GrainReference_547E1673 : global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673>
    {
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_547E1673> _arg0;
        public Activator_Invokable_IRequestShapeGrain_GrainReference_547E1673(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_547E1673> arg0)
        {
            _arg0 = arg0;
        }

        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 Create() => _arg0.TryGet(out var item) ? item : new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673(_arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_7597183B : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.Int64Codec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B instance)
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
                    instance.arg0 = global::Orleans.Serialization.Codecs.Int64Codec.ReadValue(ref reader, header);
                    reader.ReadFieldHeader(ref header);
                }

                reader.ConsumeEndBaseOrEndObject(ref header);
                break;
            }
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B @value)
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
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_7597183B : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B();
            result.arg0 = original.arg0;
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_D2239DF3 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3);
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3> _activator;
        public Codec_Invokable_IRequestShapeGrain_GrainReference_D2239DF3(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 instance)
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
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = _activator.Create();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_D2239DF3 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3> _activator;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = _activator.Create();
            result.arg0 = original.arg0;
            return result;
        }

        public Copier_Invokable_IRequestShapeGrain_GrainReference_D2239DF3(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_Invokable_IRequestShapeGrain_GrainReference_D2239DF3 : global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3>
    {
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_D2239DF3> _arg0;
        public Activator_Invokable_IRequestShapeGrain_GrainReference_D2239DF3(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_D2239DF3> arg0)
        {
            _arg0 = arg0;
        }

        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 Create() => _arg0.TryGet(out var item) ? item : new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3(_arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC);
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> _activator;
        public Codec_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC instance)
        {
            reader.ConsumeEndBaseOrEndObject();
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC @value)
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
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = _activator.Create();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC>
    {
        private readonly global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> _activator;
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = _activator.Create();
            return result;
        }

        public Copier_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC(global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> _activator)
        {
            this._activator = OrleansGeneratedCodeHelper.UnwrapService(this, _activator);
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Activator_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC : global::Orleans.Serialization.Activators.IActivator<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC>
    {
        private readonly global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> _arg0;
        public Activator_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC(global::Orleans.Serialization.Invocation.InvokablePool<Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC> arg0)
        {
            _arg0 = arg0;
        }

        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC Create() => _arg0.TryGet(out var item) ? item : new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC(_arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_179DFF79));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_3637890A));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_547E1673));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_7597183B));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_D2239DF3));
            config.AddSerializer(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_179DFF79));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_3637890A));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_547E1673));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_7597183B));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_D2239DF3));
            config.AddCopier(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC));
            config.AddInterfaceProxy(typeof(OrleansCodeGen.TestProject.Proxy_IRequestShapeGrain));
            config.AddInterface(typeof(global::TestProject.IRequestShapeGrain));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_Invokable_IRequestShapeGrain_GrainReference_179DFF79));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_Invokable_IRequestShapeGrain_GrainReference_3637890A));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_Invokable_IRequestShapeGrain_GrainReference_547E1673));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_Invokable_IRequestShapeGrain_GrainReference_D2239DF3));
            config.AddActivator(typeof(OrleansCodeGen.TestProject.Activator_Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC));
            var n1 = config.CompoundTypeAliases.Add("inv");
            var n2 = n1.Add(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.Add(typeof(global::TestProject.IRequestShapeGrain));
            n3.Add("7597183B", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B));
            n3.Add("EB0D9BAC", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_EB0D9BAC));
            n3.Add("547E1673", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673));
            n3.Add("179DFF79", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79));
            n3.Add("3637890A", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A));
            n3.Add("D2239DF3", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3));
        }
    }
}
#pragma warning restore