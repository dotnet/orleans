using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Orleans.CodeGenerator
{
    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "These property names reflect type names.")]
    internal class WellKnownTypes
    {
        public static WellKnownTypes FromCompilation(Compilation compilation)
        {
            return new WellKnownTypes
            {
                AbstractionsAssembly = Type("Orleans.IGrain").ContainingAssembly,
                Action_2 = Type("System.Action`2"),
                AlwaysInterleaveAttribute = Type("Orleans.Concurrency.AlwaysInterleaveAttribute"),
                ArgumentNullException = Type("System.ArgumentNullException"),
                CopierMethodAttribute = Type("Orleans.CodeGeneration.CopierMethodAttribute"),
                DeserializerMethodAttribute = Type("Orleans.CodeGeneration.DeserializerMethodAttribute"),
                Delegate = compilation.GetSpecialType(SpecialType.System_Delegate),
                Exception = Type("System.Exception"),
                ExcludeFromCodeCoverageAttribute = Type("System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute"),
                FormatterServices = Type("System.Runtime.Serialization.FormatterServices"),
                FieldInfo = Type("System.Reflection.FieldInfo"),
                Func_2 = Type("System.Func`2"),
                GeneratedCodeAttribute = Type("System.CodeDom.Compiler.GeneratedCodeAttribute"),
                Grain = Type("Orleans.Grain"),
                GrainFactoryBase = Type("Orleans.CodeGeneration.GrainFactoryBase"),
                GrainOfT = Type("Orleans.Grain`1"),
                GrainReference = Type("Orleans.Runtime.GrainReference"),
                GrainReferenceAttribute = Type("Orleans.CodeGeneration.GrainReferenceAttribute"),
                IAddressable = Type("Orleans.Runtime.IAddressable"),
                ICopyContext = Type("Orleans.Serialization.ICopyContext"),
                IDeserializationContext = Type("Orleans.Serialization.IDeserializationContext"),
                IFieldUtils = Type("Orleans.Serialization.IFieldUtils"),
                IGrain = Type("Orleans.IGrain"),
                IGrainExtension = Type("Orleans.Runtime.IGrainExtension"),
                IGrainExtensionMethodInvoker = Type("Orleans.CodeGeneration.IGrainExtensionMethodInvoker"),
                IGrainMethodInvoker = Type("Orleans.CodeGeneration.IGrainMethodInvoker"),
                IGrainObserver = Type("Orleans.IGrainObserver"),
                IGrainWithGuidCompoundKey = Type("Orleans.IGrainWithGuidCompoundKey"),
                IGrainWithGuidKey = Type("Orleans.IGrainWithGuidKey"),
                IGrainWithIntegerCompoundKey = Type("Orleans.IGrainWithIntegerCompoundKey"),
                IGrainWithIntegerKey = Type("Orleans.IGrainWithIntegerKey"),
                IGrainWithStringKey = Type("Orleans.IGrainWithStringKey"),
                Immutable_1 = Type("Orleans.Concurrency.Immutable`1"),
                ImmutableAttribute = Type("Orleans.Concurrency.ImmutableAttribute"),
                Int32 = compilation.GetSpecialType(SpecialType.System_Int32),
                IntPtr = compilation.GetSpecialType(SpecialType.System_IntPtr),
                InvokeMethodOptions = Type("Orleans.CodeGeneration.InvokeMethodOptions"),
                InvokeMethodRequest = Type("Orleans.CodeGeneration.InvokeMethodRequest"),
                IOnDeserialized = Type("Orleans.Serialization.IOnDeserialized"),
                ISerializationContext = Type("Orleans.Serialization.ISerializationContext"),
                ISystemTarget = Type("Orleans.ISystemTarget"),
                MarshalByRefObject = Type("System.MarshalByRefObject"),
                MethodInvokerAttribute = Type("Orleans.CodeGeneration.MethodInvokerAttribute"),
                NonSerializedAttribute = Type("System.NonSerializedAttribute"),
                NotImplementedException = Type("System.NotImplementedException"),
                Object = compilation.GetSpecialType(SpecialType.System_Object),
                ObsoleteAttribute = Type("System.ObsoleteAttribute"),
                OneWayAttribute = Type("Orleans.Concurrency.OneWayAttribute"),
                ReadOnlyAttribute = Type("Orleans.Concurrency.ReadOnlyAttribute"),
                ReentrantAttribute = Type("Orleans.Concurrency.ReentrantAttribute"),
                SerializableAttribute = Type("System.SerializableAttribute"),
                SerializerAttribute = Type("Orleans.CodeGeneration.SerializerAttribute"),
                SerializerMethodAttribute = Type("Orleans.CodeGeneration.SerializerMethodAttribute"),
                StatelessWorkerAttribute = Type("Orleans.Concurrency.StatelessWorkerAttribute"),
                SerializerFeature = Type("Orleans.Serialization.SerializerFeature"),
                String = compilation.GetSpecialType(SpecialType.System_String),
                Task = Type("System.Threading.Tasks.Task"),
                Task_1 = Type("System.Threading.Tasks.Task`1"),
                TimeSpan = Type("System.TimeSpan"),
                IPAddress = Type("System.Net.IPAddress"),
                IPEndPoint = Type("System.Net.IPEndPoint"),
                SiloAddress = Type("Orleans.Runtime.SiloAddress"),
                GrainId = Type("Orleans.Runtime.GrainId"),
                GrainInterfaceMetadata = Type("Orleans.Metadata.GrainInterfaceMetadata"),
                GrainClassMetadata = Type("Orleans.Metadata.GrainClassMetadata"),
                IFeaturePopulator_1 = Type("Orleans.Metadata.IFeaturePopulator`1"),
                FeaturePopulatorAttribute = Type("Orleans.Metadata.FeaturePopulatorAttribute"),
                GrainClassFeature = Type("Orleans.Metadata.GrainClassFeature"),
                GrainInterfaceFeature = Type("Orleans.Metadata.GrainInterfaceFeature"),
                ActivationId = Type("Orleans.Runtime.ActivationId"),
                ActivationAddress = Type("Orleans.Runtime.ActivationAddress"),
                CorrelationId = OptionalType("Orleans.Runtime.CorrelationId"),
                CancellationToken = Type("System.Threading.CancellationToken"),
                TransactionAttribute = Type("Orleans.TransactionAttribute"),
                TransactionOption = Type("Orleans.TransactionOption"),
                Type = Type("System.Type"),
                TypeCodeOverrideAttribute = Type("Orleans.CodeGeneration.TypeCodeOverrideAttribute"),
                MethodIdAttribute = Type("Orleans.CodeGeneration.MethodIdAttribute"),
                UInt16 = compilation.GetSpecialType(SpecialType.System_UInt16),
                UIntPtr = compilation.GetSpecialType(SpecialType.System_UIntPtr),
                UnorderedAttribute = Type("Orleans.Concurrency.UnorderedAttribute"),
                ValueTypeSetter_2 = Type("Orleans.Serialization.ValueTypeSetter`2"),
                VersionAttribute = Type("Orleans.CodeGeneration.VersionAttribute"),
                Void = compilation.GetSpecialType(SpecialType.System_Void),
                GenericMethodInvoker = OptionalType("Orleans.CodeGeneration.GenericMethodInvoker"),
                KnownAssemblyAttribute = Type("Orleans.CodeGeneration.KnownAssemblyAttribute"),
                KnownBaseTypeAttribute = Type("Orleans.CodeGeneration.KnownBaseTypeAttribute"),
                ConsiderForCodeGenerationAttribute = Type("Orleans.CodeGeneration.ConsiderForCodeGenerationAttribute"),
                OrleansCodeGenerationTargetAttribute = Type("Orleans.CodeGeneration.OrleansCodeGenerationTargetAttribute"),
            };

            INamedTypeSymbol Type(string type)
            {
                var result = compilation.GetTypeByMetadataName(type);
                if (result == null) throw new InvalidOperationException($"Unable to find type with metadata name \"{type}\".");
                return result;
            }

            OptionalType OptionalType(string type)
            {
                var result = compilation.GetTypeByMetadataName(type);
                if (result == null) return None.Instance;
                return new Some(result);
            }
        }

        public IAssemblySymbol AbstractionsAssembly { get; private set; }
        public INamedTypeSymbol TimeSpan { get; private set; }
        public INamedTypeSymbol GrainClassMetadata { get; private set; }
        public INamedTypeSymbol GrainInterfaceMetadata { get; private set; }
        public INamedTypeSymbol IPAddress { get; private set; }
        public INamedTypeSymbol IPEndPoint { get; private set; }
        public INamedTypeSymbol SiloAddress { get; private set; }
        public INamedTypeSymbol GrainId { get; private set; }
        public INamedTypeSymbol IFeaturePopulator_1 { get; private set; }
        public INamedTypeSymbol FeaturePopulatorAttribute { get; private set; }
        public INamedTypeSymbol GrainInterfaceFeature { get; private set; }
        public INamedTypeSymbol ActivationId { get; private set; }
        public INamedTypeSymbol ActivationAddress { get; private set; }
        public OptionalType CorrelationId { get; private set; }
        public INamedTypeSymbol CancellationToken { get; private set; }
        public INamedTypeSymbol Action_2 { get; private set; }
        public INamedTypeSymbol AlwaysInterleaveAttribute { get; private set; }
        public INamedTypeSymbol ArgumentNullException { get; private set; }
        public INamedTypeSymbol CopierMethodAttribute { get; private set; }
        public INamedTypeSymbol Delegate { get; private set; }
        public INamedTypeSymbol DeserializerMethodAttribute { get; private set; }
        public INamedTypeSymbol Exception { get; private set; }
        public INamedTypeSymbol ExcludeFromCodeCoverageAttribute { get; private set; }
        public INamedTypeSymbol FormatterServices { get; private set; }
        public INamedTypeSymbol FieldInfo { get; private set; }
        public INamedTypeSymbol Func_2 { get; private set; }
        public INamedTypeSymbol GeneratedCodeAttribute { get; private set; }
        public OptionalType GenericMethodInvoker { get; private set; }
        public INamedTypeSymbol Grain { get; private set; }
        public INamedTypeSymbol GrainFactoryBase { get; private set; }
        public INamedTypeSymbol GrainOfT { get; private set; }
        public INamedTypeSymbol GrainReference { get; private set; }
        public INamedTypeSymbol GrainReferenceAttribute { get; private set; }
        public INamedTypeSymbol IAddressable { get; private set; }
        public INamedTypeSymbol ICopyContext { get; private set; }
        public INamedTypeSymbol IDeserializationContext { get; private set; }
        public INamedTypeSymbol IFieldUtils { get; private set; }
        public INamedTypeSymbol IGrain { get; private set; }
        public INamedTypeSymbol IGrainExtension { get; private set; }
        public INamedTypeSymbol IGrainExtensionMethodInvoker { get; private set; }
        public INamedTypeSymbol GrainClassFeature { get; private set; }
        public INamedTypeSymbol SerializerFeature { get; private set; }
        public INamedTypeSymbol IGrainMethodInvoker { get; private set; }
        public INamedTypeSymbol IGrainObserver { get; private set; }
        public INamedTypeSymbol IGrainWithGuidCompoundKey { get; private set; }
        public INamedTypeSymbol IGrainWithGuidKey { get; private set; }
        public INamedTypeSymbol IGrainWithIntegerCompoundKey { get; private set; }
        public INamedTypeSymbol IGrainWithIntegerKey { get; private set; }
        public INamedTypeSymbol IGrainWithStringKey { get; private set; }
        public INamedTypeSymbol Immutable_1 { get; private set; }
        public INamedTypeSymbol ImmutableAttribute { get; private set; }
        public INamedTypeSymbol Int32 { get; private set; }
        public INamedTypeSymbol IntPtr { get; private set; }
        public INamedTypeSymbol InvokeMethodOptions { get; private set; }
        public INamedTypeSymbol InvokeMethodRequest { get; private set; }
        public INamedTypeSymbol IOnDeserialized { get; private set; }
        public INamedTypeSymbol ISerializationContext { get; private set; }
        public INamedTypeSymbol ISystemTarget { get; private set; }
        public INamedTypeSymbol MarshalByRefObject { get; private set; }
        public INamedTypeSymbol MethodInvokerAttribute { get; private set; }
        public INamedTypeSymbol NonSerializedAttribute { get; private set; }
        public INamedTypeSymbol NotImplementedException { get; private set; }
        public INamedTypeSymbol Object { get; private set; }
        public INamedTypeSymbol ObsoleteAttribute { get; private set; }
        public INamedTypeSymbol OneWayAttribute { get; private set; }
        public INamedTypeSymbol ReadOnlyAttribute { get; private set; }
        public INamedTypeSymbol ReentrantAttribute { get; private set; }
        public INamedTypeSymbol SerializableAttribute { get; private set; }
        public INamedTypeSymbol SerializerAttribute { get; private set; }
        public INamedTypeSymbol SerializerMethodAttribute { get; private set; }
        public INamedTypeSymbol StatelessWorkerAttribute { get; private set; }
        public INamedTypeSymbol String { get; private set; }
        public INamedTypeSymbol Task { get; private set; }
        public INamedTypeSymbol Task_1 { get; private set; }
        public INamedTypeSymbol TransactionAttribute { get; private set; }
        public INamedTypeSymbol TransactionOption { get; private set; }
        public INamedTypeSymbol Type { get; private set; }
        public INamedTypeSymbol TypeCodeOverrideAttribute { get; private set; }
        public INamedTypeSymbol MethodIdAttribute { get; private set; }
        public INamedTypeSymbol UInt16 { get; private set; }
        public INamedTypeSymbol UIntPtr { get; private set; }
        public INamedTypeSymbol UnorderedAttribute { get; private set; }
        public INamedTypeSymbol ValueTypeSetter_2 { get; private set; }
        public INamedTypeSymbol VersionAttribute { get; private set; }
        public INamedTypeSymbol Void { get; private set; }
        public INamedTypeSymbol KnownAssemblyAttribute { get; private set; }
        public INamedTypeSymbol KnownBaseTypeAttribute { get; private set; }
        public INamedTypeSymbol ConsiderForCodeGenerationAttribute { get; private set; }
        public INamedTypeSymbol OrleansCodeGenerationTargetAttribute { get; private set; }
        public class OptionalType { }

        public class None : OptionalType
        {
            public static None Instance { get; } = new None();
        }

        public class Some : OptionalType
        {
            public Some(INamedTypeSymbol value)
            {
                Value = value;
            }

            public INamedTypeSymbol Value { get; }
        }
    }
}