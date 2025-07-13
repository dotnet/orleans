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
    [global::Orleans.CompoundTypeAliasAttribute("inv", typeof(global::Orleans.Runtime.GrainReference), typeof(global::TestProject.IResponseTimeoutGrain), "6BE752C8")]
    public sealed class Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 : global::Orleans.Runtime.TaskRequest<string>
    {
        public string arg0;
        global::TestProject.IResponseTimeoutGrain _target;
        private static readonly global::System.Reflection.MethodInfo MethodBackingField = OrleansGeneratedCodeHelper.GetMethodInfoOrDefault(typeof(global::TestProject.IResponseTimeoutGrain), "LongRunningMethod", null, new[] { typeof(string) });
        private static readonly global::System.TimeSpan _responseTimeoutValue = global::System.TimeSpan.FromTicks(100000000L);
        public override global::System.TimeSpan? GetDefaultResponseTimeout() => _responseTimeoutValue;
        public override int GetArgumentCount() => 1;
        public override string GetMethodName() => "LongRunningMethod";
        public override string GetInterfaceName() => "TestProject.IResponseTimeoutGrain";
        public override string GetActivityName() => "IResponseTimeoutGrain/LongRunningMethod";
        public override global::System.Type GetInterfaceType() => typeof(global::TestProject.IResponseTimeoutGrain);
        public override global::System.Reflection.MethodInfo GetMethod() => MethodBackingField;
        public override void SetTarget(global::Orleans.Serialization.Invocation.ITargetHolder holder) => _target = holder.GetTarget<global::TestProject.IResponseTimeoutGrain>();
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

        protected override global::System.Threading.Tasks.Task<string> InvokeInner() => _target.LongRunningMethod(arg0);
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Proxy_IResponseTimeoutGrain : global::Orleans.Runtime.GrainReference, global::TestProject.IResponseTimeoutGrain
    {
        public Proxy_IResponseTimeoutGrain(global::Orleans.Runtime.GrainReferenceShared arg0, global::Orleans.Runtime.IdSpan arg1) : base(arg0, arg1)
        {
        }

        global::System.Threading.Tasks.Task<string> global::TestProject.IResponseTimeoutGrain.LongRunningMethod(string arg0)
        {
            var request = new OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8();
            request.arg0 = arg0;
            return base.InvokeAsync<string>(request).AsTask();
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Codec_Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 : global::Orleans.Serialization.Codecs.IFieldCodec<OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8>
    {
        private readonly global::System.Type _codecFieldType = typeof(OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8);
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 instance)
            where TBufferWriter : global::System.Buffers.IBufferWriter<byte>
        {
            global::Orleans.Serialization.Codecs.StringCodec.WriteField(ref writer, 0U, instance.arg0);
        }

        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 instance)
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
        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, global::System.Type expectedType, OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 @value)
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
        public OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8, TReaderInput>(ref reader, field);
            field.EnsureWireTypeTagDelimited();
            var result = new OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8();
            ReferenceCodec.MarkValueField(reader.Session);
            Deserialize(ref reader, result);
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    public sealed class Copier_Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 : global::Orleans.Serialization.Cloning.IDeepCopier<OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8>
    {
        [global::System.Runtime.CompilerServices.MethodImplAttribute(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 DeepCopy(OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8 original, global::Orleans.Serialization.Cloning.CopyContext context)
        {
            if (original is null)
                return null;
            var result = new OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8();
            result.arg0 = original.arg0;
            return result;
        }
    }

    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("OrleansCodeGen", "9.0.0.0"), global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Never), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute]
    internal sealed class Metadata_TestProject : global::Orleans.Serialization.Configuration.TypeManifestProviderBase
    {
        protected override void ConfigureInner(global::Orleans.Serialization.Configuration.TypeManifestOptions config)
        {
            config.Serializers.Add(typeof(OrleansCodeGen.TestProject.Codec_Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8));
            config.Copiers.Add(typeof(OrleansCodeGen.TestProject.Copier_Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8));
            config.InterfaceProxies.Add(typeof(OrleansCodeGen.TestProject.Proxy_IResponseTimeoutGrain));
            config.Interfaces.Add(typeof(global::TestProject.IResponseTimeoutGrain));
            config.InterfaceImplementations.Add(typeof(global::TestProject.ResponseTimeoutGrain));
            var n1 = config.CompoundTypeAliases.Add("inv");
            var n2 = n1.Add(typeof(global::Orleans.Runtime.GrainReference));
            var n3 = n2.Add(typeof(global::TestProject.IResponseTimeoutGrain));
            n3.Add("6BE752C8", typeof(OrleansCodeGen.TestProject.Invokable_IResponseTimeoutGrain_GrainReference_6BE752C8));
        }
    }
}
#pragma warning restore CS1591, RS0016, RS0041
