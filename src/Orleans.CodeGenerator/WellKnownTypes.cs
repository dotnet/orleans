using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "These property names reflect type names.")]
    internal class WellKnownTypes
    {
        public WellKnownTypes(Compilation compilation)
        {
            Attribute = Type("System.Attribute");
            ApplicationPartAttribute = Type("Orleans.Metadata.ApplicationPartAttribute");
            Action_2 = Type("System.Action`2");
            AlwaysInterleaveAttribute = Type("Orleans.Concurrency.AlwaysInterleaveAttribute");
            CopierMethodAttribute = Type("Orleans.CodeGeneration.CopierMethodAttribute");
            DeserializerMethodAttribute = Type("Orleans.CodeGeneration.DeserializerMethodAttribute");
            Delegate = compilation.GetSpecialType(SpecialType.System_Delegate);
            DebuggerStepThroughAttribute = Type("System.Diagnostics.DebuggerStepThroughAttribute");
            Exception = Type("System.Exception");
            ExcludeFromCodeCoverageAttribute = Type("System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute");
            FormatterServices = Type("System.Runtime.Serialization.FormatterServices");
            FieldInfo = Type("System.Reflection.FieldInfo");
            Func_2 = Type("System.Func`2");
            GeneratedCodeAttribute = Type("System.CodeDom.Compiler.GeneratedCodeAttribute");
            Grain = Type("Orleans.Grain");
            GrainFactoryBase = Type("Orleans.CodeGeneration.GrainFactoryBase");
            GrainOfT = Type("Orleans.Grain`1");
            GrainReference = Type("Orleans.Runtime.GrainReference");
            GrainReferenceAttribute = Type("Orleans.CodeGeneration.GrainReferenceAttribute");
            IAddressable = Type("Orleans.Runtime.IAddressable");
            IGrainContext = Type("Orleans.Runtime.IGrainContext");
            ICopyContext = Type("Orleans.Serialization.ICopyContext");
            IDeserializationContext = Type("Orleans.Serialization.IDeserializationContext");
            IFieldUtils = Type("Orleans.Serialization.IFieldUtils");
            IGrain = Type("Orleans.IGrain");
            IGrainExtension = Type("Orleans.Runtime.IGrainExtension");
            IGrainExtensionMethodInvoker = Type("Orleans.CodeGeneration.IGrainExtensionMethodInvoker");
            IGrainMethodInvoker = Type("Orleans.CodeGeneration.IGrainMethodInvoker");
            IGrainObserver = Type("Orleans.IGrainObserver");
            IGrainWithGuidCompoundKey = Type("Orleans.IGrainWithGuidCompoundKey");
            IGrainWithGuidKey = Type("Orleans.IGrainWithGuidKey");
            IGrainWithIntegerCompoundKey = Type("Orleans.IGrainWithIntegerCompoundKey");
            IGrainWithIntegerKey = Type("Orleans.IGrainWithIntegerKey");
            IGrainWithStringKey = Type("Orleans.IGrainWithStringKey");
            Immutable_1 = Type("Orleans.Concurrency.Immutable`1");
            ImmutableAttribute = Type("Orleans.Concurrency.ImmutableAttribute");
            Int32 = compilation.GetSpecialType(SpecialType.System_Int32);
            InvokeMethodOptions = Type("Orleans.CodeGeneration.InvokeMethodOptions");
            InvokeMethodRequest = Type("Orleans.CodeGeneration.InvokeMethodRequest");
            IOnDeserialized = Type("Orleans.Serialization.IOnDeserialized");
            ISerializationContext = Type("Orleans.Serialization.ISerializationContext");
            ISystemTarget = Type("Orleans.ISystemTarget");
            MarshalByRefObject = Type("System.MarshalByRefObject");
            MethodInvokerAttribute = Type("Orleans.CodeGeneration.MethodInvokerAttribute");
            NonSerializedAttribute = Type("System.NonSerializedAttribute");
            NotImplementedException = Type("System.NotImplementedException");
            Object = compilation.GetSpecialType(SpecialType.System_Object);
            ObsoleteAttribute = Type("System.ObsoleteAttribute");
            OneWayAttribute = Type("Orleans.Concurrency.OneWayAttribute");
            ReadOnlyAttribute = Type("Orleans.Concurrency.ReadOnlyAttribute");
            SerializableAttribute = Type("System.SerializableAttribute");
            SerializerAttribute = Type("Orleans.CodeGeneration.SerializerAttribute");
            SerializerMethodAttribute = Type("Orleans.CodeGeneration.SerializerMethodAttribute");
            SerializerFeature = Type("Orleans.Serialization.SerializerFeature");
            String = compilation.GetSpecialType(SpecialType.System_String);
            Task = Type("System.Threading.Tasks.Task");
            Task_1 = Type("System.Threading.Tasks.Task`1");
            ValueTask = OptionalType("System.Threading.Tasks.ValueTask");
            TimeSpan = Type("System.TimeSpan");
            IPAddress = Type("System.Net.IPAddress");
            IPEndPoint = Type("System.Net.IPEndPoint");
            SiloAddress = Type("Orleans.Runtime.SiloAddress");
            GrainId = Type("Orleans.Runtime.GrainId");
            GrainInterfaceMetadata = Type("Orleans.Metadata.GrainInterfaceMetadata");
            GrainClassMetadata = Type("Orleans.Metadata.GrainClassMetadata");
            IFeaturePopulator_1 = Type("Orleans.Metadata.IFeaturePopulator`1");
            FeaturePopulatorAttribute = Type("Orleans.Metadata.FeaturePopulatorAttribute");
            GrainClassFeature = Type("Orleans.Metadata.GrainClassFeature");
            GrainInterfaceFeature = Type("Orleans.Metadata.GrainInterfaceFeature");
            ActivationId = Type("Orleans.Runtime.ActivationId");
            ActivationAddress = Type("Orleans.Runtime.ActivationAddress");
            CorrelationId = OptionalType("Orleans.Runtime.CorrelationId");
            CancellationToken = Type("System.Threading.CancellationToken");
            TransactionAttribute = Type("Orleans.TransactionAttribute");
            TransactionOption = Type("Orleans.TransactionOption");
            this.Type = Type("System.Type");
            TypeCodeOverrideAttribute = Type("Orleans.CodeGeneration.TypeCodeOverrideAttribute");
            MethodIdAttribute = Type("Orleans.CodeGeneration.MethodIdAttribute");
            UInt16 = compilation.GetSpecialType(SpecialType.System_UInt16);
            UnorderedAttribute = Type("Orleans.Concurrency.UnorderedAttribute");
            ValueTypeSetter_2 = Type("Orleans.Serialization.ValueTypeSetter`2");
            VersionAttribute = Type("Orleans.CodeGeneration.VersionAttribute");
            Void = compilation.GetSpecialType(SpecialType.System_Void);
            GenericMethodInvoker = OptionalType("Orleans.CodeGeneration.GenericMethodInvoker");
            KnownAssemblyAttribute = Type("Orleans.CodeGeneration.KnownAssemblyAttribute");
            KnownBaseTypeAttribute = Type("Orleans.CodeGeneration.KnownBaseTypeAttribute");
            ConsiderForCodeGenerationAttribute = Type("Orleans.CodeGeneration.ConsiderForCodeGenerationAttribute");
            OrleansCodeGenerationTargetAttribute = Type("Orleans.CodeGeneration.OrleansCodeGenerationTargetAttribute");
            SupportedRefAsmBaseTypes = new[]
            {
                Type("System.Collections.Generic.EqualityComparer`1"),
                Type("System.Collections.Generic.Comparer`1")
            };
            TupleTypes = new[]
            {
                Type("System.Tuple`1"),
                Type("System.Tuple`2"),
                Type("System.Tuple`3"),
                Type("System.Tuple`4"),
                Type("System.Tuple`5"),
                Type("System.Tuple`6"),
                Type("System.Tuple`7"),
                Type("System.Tuple`8"),
            };

            INamedTypeSymbol Type(string type)
            {
                var result = ResolveType(type);
                if (result == null)
                {
                    throw new InvalidOperationException($"Unable to find type with metadata name \"{type}\".");
                }
                return result;
            }

            OptionalType OptionalType(string type)
            {
                var result = ResolveType(type);
                if (result == null) return None.Instance;
                return new Some(result);
            }

            INamedTypeSymbol ResolveType(string type)
            {
                var result = compilation.GetTypeByMetadataName(type);
                if (result == null)
                {
                    foreach (var reference in compilation.References)
                    {
                        var asm = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                        if (asm == null) continue;
                        result = asm.GetTypeByMetadataName(type);
                        if (result != null) break;
                    }
                }

                return result;
            }
        }

        public INamedTypeSymbol[] SupportedRefAsmBaseTypes { get; }
        public INamedTypeSymbol[] TupleTypes { get; }
        public INamedTypeSymbol Attribute { get; }
        public INamedTypeSymbol ApplicationPartAttribute { get; }
        public INamedTypeSymbol TimeSpan { get; }
        public INamedTypeSymbol GrainClassMetadata { get; }
        public INamedTypeSymbol GrainInterfaceMetadata { get; }
        public INamedTypeSymbol IPAddress { get; }
        public INamedTypeSymbol IPEndPoint { get; }
        public INamedTypeSymbol SiloAddress { get; }
        public INamedTypeSymbol GrainId { get; }
        public INamedTypeSymbol IFeaturePopulator_1 { get; }
        public INamedTypeSymbol FeaturePopulatorAttribute { get; }
        public INamedTypeSymbol GrainInterfaceFeature { get; }
        public INamedTypeSymbol ActivationId { get; }
        public INamedTypeSymbol ActivationAddress { get; }
        public OptionalType CorrelationId { get; }
        public INamedTypeSymbol CancellationToken { get; }
        public INamedTypeSymbol Action_2 { get; }
        public INamedTypeSymbol AlwaysInterleaveAttribute { get; }
        public INamedTypeSymbol CopierMethodAttribute { get; }
        public INamedTypeSymbol Delegate { get; }
        public INamedTypeSymbol DeserializerMethodAttribute { get; }
        public INamedTypeSymbol DebuggerStepThroughAttribute { get; }
        public INamedTypeSymbol Exception { get; }
        public INamedTypeSymbol ExcludeFromCodeCoverageAttribute { get; }
        public INamedTypeSymbol FormatterServices { get; }
        public INamedTypeSymbol FieldInfo { get; }
        public INamedTypeSymbol Func_2 { get; }
        public INamedTypeSymbol GeneratedCodeAttribute { get; }
        public OptionalType GenericMethodInvoker { get; }
        public INamedTypeSymbol Grain { get; }
        public INamedTypeSymbol GrainFactoryBase { get; }
        public INamedTypeSymbol GrainOfT { get; }
        public INamedTypeSymbol GrainReference { get; }
        public INamedTypeSymbol GrainReferenceAttribute { get; }
        public INamedTypeSymbol IAddressable { get; }
        public INamedTypeSymbol ICopyContext { get; }
        public INamedTypeSymbol IGrainContext { get; }
        public INamedTypeSymbol IDeserializationContext { get; }
        public INamedTypeSymbol IFieldUtils { get; }
        public INamedTypeSymbol IGrain { get; }
        public INamedTypeSymbol IGrainExtension { get; }
        public INamedTypeSymbol IGrainExtensionMethodInvoker { get; }
        public INamedTypeSymbol GrainClassFeature { get; }
        public INamedTypeSymbol SerializerFeature { get; }
        public INamedTypeSymbol IGrainMethodInvoker { get; }
        public INamedTypeSymbol IGrainObserver { get; }
        public INamedTypeSymbol IGrainWithGuidCompoundKey { get; }
        public INamedTypeSymbol IGrainWithGuidKey { get; }
        public INamedTypeSymbol IGrainWithIntegerCompoundKey { get; }
        public INamedTypeSymbol IGrainWithIntegerKey { get; }
        public INamedTypeSymbol IGrainWithStringKey { get; }
        public INamedTypeSymbol Immutable_1 { get; }
        public INamedTypeSymbol ImmutableAttribute { get; }
        public INamedTypeSymbol Int32 { get; }
        public INamedTypeSymbol InvokeMethodOptions { get; }
        public INamedTypeSymbol InvokeMethodRequest { get; }
        public INamedTypeSymbol IOnDeserialized { get; }
        public INamedTypeSymbol ISerializationContext { get; }
        public INamedTypeSymbol ISystemTarget { get; }
        public INamedTypeSymbol MarshalByRefObject { get; }
        public INamedTypeSymbol MethodInvokerAttribute { get; }
        public INamedTypeSymbol NonSerializedAttribute { get; }
        public INamedTypeSymbol NotImplementedException { get; }
        public INamedTypeSymbol Object { get; }
        public INamedTypeSymbol ObsoleteAttribute { get; }
        public INamedTypeSymbol OneWayAttribute { get; }
        public INamedTypeSymbol ReadOnlyAttribute { get; }
        public INamedTypeSymbol SerializableAttribute { get; }
        public INamedTypeSymbol SerializerAttribute { get; }
        public INamedTypeSymbol SerializerMethodAttribute { get; }
        public INamedTypeSymbol String { get; }
        public INamedTypeSymbol Task { get; }
        public INamedTypeSymbol Task_1 { get; }
        public OptionalType ValueTask { get; }
        public INamedTypeSymbol TransactionAttribute { get; }
        public INamedTypeSymbol TransactionOption { get; }
        public INamedTypeSymbol Type { get; }
        public INamedTypeSymbol TypeCodeOverrideAttribute { get; }
        public INamedTypeSymbol MethodIdAttribute { get; }
        public INamedTypeSymbol UInt16 { get; }
        public INamedTypeSymbol UnorderedAttribute { get; }
        public INamedTypeSymbol ValueTypeSetter_2 { get; }
        public INamedTypeSymbol VersionAttribute { get; }
        public INamedTypeSymbol Void { get; }
        public INamedTypeSymbol KnownAssemblyAttribute { get; }
        public INamedTypeSymbol KnownBaseTypeAttribute { get; }
        public INamedTypeSymbol ConsiderForCodeGenerationAttribute { get; }
        public INamedTypeSymbol OrleansCodeGenerationTargetAttribute { get; }

        public abstract class OptionalType { }

        public sealed class None : OptionalType
        {
            public static None Instance { get; } = new None();
        }

        public sealed class Some : OptionalType
        {
            public Some(INamedTypeSymbol value)
            {
                Value = value;
            }

            public INamedTypeSymbol Value { get; }
        }
    }
}