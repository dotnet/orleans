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

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IRequestShapeGrain), "179DFF79")]
    public sealed class Invokable_IRequestShapeGrain_GrainReference_179DFF79 : global::Orleans.Runtime.TaskRequest<int>
    {
        public byte[] arg0;
        global::TestProject.IRequestShapeGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IRequestShapeGrain), "TaskOfTMethod", null, new[] { typeof(byte[]) });
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
    internal sealed class Proxy_IRequestShapeGrain : global::Orleans.Runtime.GrainReference, global::TestProject.IRequestShapeGrain
    {
        public Proxy_IRequestShapeGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.ValueTask global::TestProject.IRequestShapeGrain.ValueTaskMethod(int arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A();
            request.arg0 = arg0;
            return base.InvokeAsync(request);
        }

        global::System.Threading.Tasks.ValueTask<int> global::TestProject.IRequestShapeGrain.ValueTaskOfTMethod(string arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3();
            request.arg0 = arg0;
            return base.InvokeAsync<int>(request);
        }

        global::System.Threading.Tasks.Task global::TestProject.IRequestShapeGrain.TaskMethod(object arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673();
            using var copyContext = base.CopyContextPool.GetContext();
            request.arg0 = global::Orleans.Serialization.Codecs.ObjectCopier.DeepCopy(arg0, copyContext);
            return base.InvokeAsync(request).AsTask();
        }

        global::System.Threading.Tasks.Task<int> global::TestProject.IRequestShapeGrain.TaskOfTMethod(byte[] arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79();
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
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_179DFF79 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79();
            result.arg0 = global::Orleans.Serialization.Codecs.ByteArrayCopier.DeepCopy(original.arg0, context);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_3637890A : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A);
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
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_3637890A : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A();
            result.arg0 = original.arg0;
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IRequestShapeGrain_GrainReference_547E1673 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673);
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
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_547E1673 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673();
            result.arg0 = global::Orleans.Serialization.Codecs.ObjectCopier.DeepCopy(original.arg0, context);
            return result;
        }
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
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IRequestShapeGrain_GrainReference_D2239DF3 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 DeepCopy(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3();
            result.arg0 = original.arg0;
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "10.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_179DFF79));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_3637890A));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_547E1673));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_7597183B));
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IRequestShapeGrain_GrainReference_D2239DF3));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_179DFF79));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_3637890A));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_547E1673));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_7597183B));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IRequestShapeGrain_GrainReference_D2239DF3));
            config.InterfaceProxies.Add(typeof(OrleansCodeGen.TestProject.Proxy_IRequestShapeGrain));
            config.Interfaces.Add(typeof(global::TestProject.IRequestShapeGrain));
            var n1 = config.CompoundTypeAliases.Add("inv");
            var n2 = n1.Add(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.Add(typeof(global::TestProject.IRequestShapeGrain));
            n3.Add("7597183B", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_7597183B));
            n3.Add("547E1673", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_547E1673));
            n3.Add("179DFF79", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_179DFF79));
            n3.Add("3637890A", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_3637890A));
            n3.Add("D2239DF3", typeof(OrleansCodeGen.TestProject.Invokable_IRequestShapeGrain_GrainReference_D2239DF3));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041