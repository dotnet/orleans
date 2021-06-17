using Orleans.CodeGenerator.SyntaxGeneration;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.CodeGenerator
{
    internal class LibraryTypes
    {
        private LibraryTypes() { }

        public static LibraryTypes FromCompilation(Compilation compilation, CodeGeneratorOptions options)
        {
            return new LibraryTypes
            {
                Compilation = compilation,
                ApplicationPartAttribute = Type("Orleans.ApplicationPartAttribute"),
                Action_2 = Type("System.Action`2"),
                Byte = compilation.GetSpecialType(SpecialType.System_Byte),
                ITypeManifestProvider = Type("Orleans.Serialization.Configuration.ITypeManifestProvider"),
                Field = Type("Orleans.Serialization.WireProtocol.Field"),
                WireType = Type("Orleans.Serialization.WireProtocol.WireType"),
                FieldCodec = Type("Orleans.Serialization.Codecs.IFieldCodec"),
                FieldCodec_1 = Type("Orleans.Serialization.Codecs.IFieldCodec`1"),
                DeepCopier_1 = Type("Orleans.Serialization.Cloning.IDeepCopier`1"),
                CopyContext = Type("Orleans.Serialization.Cloning.CopyContext"),
                MethodInfo = Type("System.Reflection.MethodInfo"),
                Func_2 = Type("System.Func`2"),
                GenerateMethodSerializersAttribute = Type("Orleans.GenerateMethodSerializersAttribute"),
                GenerateSerializerAttribute = Type("Orleans.GenerateSerializerAttribute"),
                SerializationCallbacksAttribute = Type("Orleans.SerializationCallbacksAttribute"),
                IActivator_1 = Type("Orleans.Serialization.Activators.IActivator`1"),
                IBufferWriter = Type("System.Buffers.IBufferWriter`1"),
                IdAttributeTypes = options.IdAttributes.Select(Type).ToList(),
                WellKnownAliasAttribute = Type("Orleans.WellKnownAliasAttribute"),
                WellKnownIdAttribute = Type("Orleans.WellKnownIdAttribute"),
                IInvokable = Type("Orleans.Serialization.Invocation.IInvokable"),
                DefaultInvokeMethodNameAttribute = Type("Orleans.DefaultInvokeMethodNameAttribute"),
                InvokeMethodNameAttribute = Type("Orleans.InvokeMethodNameAttribute"),
                FormatterServices = Type("System.Runtime.Serialization.FormatterServices"),
                InvokableCustomInitializerAttribute = Type("Orleans.InvokableCustomInitializerAttribute"),
                DefaultInvokableBaseTypeAttribute = Type("Orleans.DefaultInvokableBaseTypeAttribute"),
                GenerateCodeForDeclaringAssemblyAttribute = Type("Orleans.GenerateCodeForDeclaringAssemblyAttribute"),
                InvokableBaseTypeAttribute = Type("Orleans.InvokableBaseTypeAttribute"),
                RegisterSerializerAttribute = Type("Orleans.RegisterSerializerAttribute"),
                GeneratedActivatorConstructorAttribute = Type("Orleans.GeneratedActivatorConstructorAttribute"),
                RegisterActivatorAttribute = Type("Orleans.RegisterActivatorAttribute"),
                RegisterCopierAttribute = Type("Orleans.RegisterCopierAttribute"),
                UseActivatorAttribute = Type("Orleans.UseActivatorAttribute"),
                SuppressReferenceTrackingAttribute = Type("Orleans.SuppressReferenceTrackingAttribute"),
                OmitDefaultMemberValuesAttribute = Type("Orleans.OmitDefaultMemberValuesAttribute"),
                Int32 = compilation.GetSpecialType(SpecialType.System_Int32),
                UInt32 = compilation.GetSpecialType(SpecialType.System_UInt32),
                InvalidOperationException = Type("System.InvalidOperationException"),
                InvokablePool = Type("Orleans.Serialization.Invocation.InvokablePool"),
                IResponseCompletionSource = Type("Orleans.Serialization.Invocation.IResponseCompletionSource"),
                ITargetHolder = Type("Orleans.Serialization.Invocation.ITargetHolder"),
                TypeManifestProviderAttribute = Type("Orleans.Serialization.Configuration.TypeManifestProviderAttribute"),
                NonSerializedAttribute = Type("System.NonSerializedAttribute"),
                Object = compilation.GetSpecialType(SpecialType.System_Object),
                ObsoleteAttribute = Type("System.ObsoleteAttribute"),
                BaseCodec_1 = Type("Orleans.Serialization.Serializers.IBaseCodec`1"),
                BaseCopier_1 = Type("Orleans.Serialization.Cloning.IBaseCopier`1"),
                Reader = Type("Orleans.Serialization.Buffers.Reader`1"),
                Request = Type("Orleans.Serialization.Invocation.Request"),
                Request_1 = Type("Orleans.Serialization.Invocation.Request`1"),
                ResponseCompletionSourcePool = Type("Orleans.Serialization.Invocation.ResponseCompletionSourcePool"),
                TypeManifestOptions = Type("Orleans.Serialization.Configuration.TypeManifestOptions"),
                SerializerSession = Type("Orleans.Serialization.Session.SerializerSession"),
                Task = Type("System.Threading.Tasks.Task"),
                Task_1 = Type("System.Threading.Tasks.Task`1"),
                TaskRequest = Type("Orleans.Serialization.Invocation.TaskRequest"),
                TaskRequest_1 = Type("Orleans.Serialization.Invocation.TaskRequest`1"),
                Type = Type("System.Type"),
                ICodecProvider = Type("Orleans.Serialization.Serializers.ICodecProvider"),
                ValueSerializer = Type("Orleans.Serialization.Serializers.IValueSerializer`1"),
                ValueTask = Type("System.Threading.Tasks.ValueTask"),
                ValueTask_1 = Type("System.Threading.Tasks.ValueTask`1"),
                ValueTypeSetter_2 = Type("Orleans.Serialization.Utilities.ValueTypeSetter`2"),
                Void = compilation.GetSpecialType(SpecialType.System_Void),
                VoidRequest = Type("Orleans.Serialization.Invocation.VoidRequest"),
                Writer = Type("Orleans.Serialization.Buffers.Writer`1"),
                IDisposable = Type("System.IDisposable"),
                FSharpSourceConstructFlagsOrDefault = TypeOrDefault("Microsoft.FSharp.Core.SourceConstructFlags"),
                FSharpCompilationMappingAttributeOrDefault = TypeOrDefault("Microsoft.FSharp.Core.CompilationMappingAttribute"),
                StaticCodecs = new List<WellKnownCodecDescription>
                {
                    new(compilation.GetSpecialType(SpecialType.System_Object), Type("Orleans.Serialization.Codecs.ObjectCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Boolean), Type("Orleans.Serialization.Codecs.BoolCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Char), Type("Orleans.Serialization.Codecs.CharCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Byte), Type("Orleans.Serialization.Codecs.ByteCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_SByte), Type("Orleans.Serialization.Codecs.SByteCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Int16), Type("Orleans.Serialization.Codecs.Int16Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_Int32), Type("Orleans.Serialization.Codecs.Int32Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_Int64), Type("Orleans.Serialization.Codecs.Int64Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_UInt16), Type("Orleans.Serialization.Codecs.UInt16Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_UInt32), Type("Orleans.Serialization.Codecs.UInt32Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_UInt64), Type("Orleans.Serialization.Codecs.UInt64Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_String), Type("Orleans.Serialization.Codecs.StringCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Object), Type("Orleans.Serialization.Codecs.ObjectCodec")),
                    new(compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Byte), 1), Type("Orleans.Serialization.Codecs.ByteArrayCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Single), Type("Orleans.Serialization.Codecs.FloatCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Double), Type("Orleans.Serialization.Codecs.DoubleCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Decimal), Type("Orleans.Serialization.Codecs.DecimalCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_DateTime), Type("Orleans.Serialization.Codecs.DateTimeCodec")),
                    new(Type("System.TimeSpan"), Type("Orleans.Serialization.Codecs.TimeSpanCodec")),
                    new(Type("System.DateTimeOffset"), Type("Orleans.Serialization.Codecs.DateTimeOffsetCodec")),
                    new(Type("System.Guid"), Type("Orleans.Serialization.Codecs.GuidCodec")),
                    new(Type("System.Type"), Type("Orleans.Serialization.Codecs.TypeSerializerCodec")),
                    new(Type("System.ReadOnlyMemory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.ReadOnlyMemoryOfByteCodec")),
                    new(Type("System.Memory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.MemoryOfByteCodec")),
                    new(Type("System.Net.IPAddress"), Type("Orleans.Serialization.Codecs.IPAddressCodec")),
                    new(Type("System.Net.IPEndPoint"), Type("Orleans.Serialization.Codecs.IPEndPointCodec")),
                },
                WellKnownCodecs = new List<WellKnownCodecDescription>
                {
                    new(Type("System.Collections.Generic.Dictionary`2"), Type("Orleans.Serialization.Codecs.DictionaryCodec`2")),
                    new(Type("System.Collections.Generic.List`1"), Type("Orleans.Serialization.Codecs.ListCodec`1")),
                },
                StaticCopiers = new List<WellKnownCopierDescription>
                {
                    //new WellKnownCopierDescription(compilation.GetSpecialType(SpecialType.System_Object), Type("Orleans.Serialization.Codecs.ObjectCopier")),
                    new(compilation.GetSpecialType(SpecialType.System_Boolean), Type("Orleans.Serialization.Codecs.BoolCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Char), Type("Orleans.Serialization.Codecs.CharCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Byte), Type("Orleans.Serialization.Codecs.ByteCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_SByte), Type("Orleans.Serialization.Codecs.SByteCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Int16), Type("Orleans.Serialization.Codecs.Int16Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_Int32), Type("Orleans.Serialization.Codecs.Int32Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_Int64), Type("Orleans.Serialization.Codecs.Int64Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_UInt16), Type("Orleans.Serialization.Codecs.UInt16Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_UInt32), Type("Orleans.Serialization.Codecs.UInt32Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_UInt64), Type("Orleans.Serialization.Codecs.UInt64Codec")),
                    new(compilation.GetSpecialType(SpecialType.System_String), Type("Orleans.Serialization.Codecs.StringCopier")),
                    new(compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Byte), 1), Type("Orleans.Serialization.Codecs.ByteArrayCopier")),
                    new(compilation.GetSpecialType(SpecialType.System_Single), Type("Orleans.Serialization.Codecs.FloatCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Double), Type("Orleans.Serialization.Codecs.DoubleCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Decimal), Type("Orleans.Serialization.Codecs.DecimalCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_DateTime), Type("Orleans.Serialization.Codecs.DateTimeCodec")),
                    new(Type("System.TimeSpan"), Type("Orleans.Serialization.Codecs.TimeSpanCopier")),
                    new(Type("System.DateTimeOffset"), Type("Orleans.Serialization.Codecs.DateTimeOffsetCopier")),
                    new(Type("System.Guid"), Type("Orleans.Serialization.Codecs.GuidCopier")),
                    new(Type("System.Type"), Type("Orleans.Serialization.Codecs.TypeCopier")),
                    new(Type("System.ReadOnlyMemory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.ReadOnlyMemoryOfByteCopier")),
                    new(Type("System.Memory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.MemoryOfByteCopier")),
                    new(Type("System.Net.IPAddress"), Type("Orleans.Serialization.Codecs.IPAddressCopier")),
                    new(Type("System.Net.IPEndPoint"), Type("Orleans.Serialization.Codecs.IPEndPointCopier")),
                },
                WellKnownCopiers = new List<WellKnownCopierDescription>
                {
                    new(Type("System.Collections.Generic.Dictionary`2"), Type("Orleans.Serialization.Codecs.DictionaryCopier`2")),
                    new(Type("System.Collections.Generic.List`1"), Type("Orleans.Serialization.Codecs.ListCopier`1")),
                },
                ImmutableTypes = new List<ITypeSymbol>
                {
                    compilation.GetSpecialType(SpecialType.System_Boolean),
                    compilation.GetSpecialType(SpecialType.System_Char),
                    compilation.GetSpecialType(SpecialType.System_Byte),
                    compilation.GetSpecialType(SpecialType.System_SByte),
                    compilation.GetSpecialType(SpecialType.System_Int16),
                    compilation.GetSpecialType(SpecialType.System_Int32),
                    compilation.GetSpecialType(SpecialType.System_Int64),
                    compilation.GetSpecialType(SpecialType.System_UInt16),
                    compilation.GetSpecialType(SpecialType.System_UInt32),
                    compilation.GetSpecialType(SpecialType.System_UInt64),
                    compilation.GetSpecialType(SpecialType.System_String),
                    compilation.GetSpecialType(SpecialType.System_Single),
                    compilation.GetSpecialType(SpecialType.System_Double),
                    compilation.GetSpecialType(SpecialType.System_Decimal),
                    compilation.GetSpecialType(SpecialType.System_DateTime),
                },
                    Exception = Type("System.Exception"),
                    ImmutableAttributes = options.ImmutableAttributes.Select(Type).ToList(),
                    ValueTuple = Type("System.ValueTuple"),
                    TimeSpan = Type("System.TimeSpan"),
                    DateTimeOffset = Type("System.DateTimeOffset"),
                    Guid = Type("System.Guid"),
                    IPAddress = Type("System.Net.IPAddress"),
                    IPEndPoint = Type("System.Net.IPEndPoint"),
                    CancellationToken = Type("System.Threading.CancellationToken"),
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
                Type("System.ValueTuple`1"),
                Type("System.ValueTuple`2"),
                Type("System.ValueTuple`3"),
                Type("System.ValueTuple`4"),
                Type("System.ValueTuple`5"),
                Type("System.ValueTuple`6"),
                Type("System.ValueTuple`7"),
                Type("System.ValueTuple`8"),
            },
            };

            INamedTypeSymbol Type(string metadataName)
            {
                var result = compilation.GetTypeByMetadataName(metadataName);
                if (result is null)
                {
                    throw new InvalidOperationException("Cannot find type with metadata name " + metadataName);
                }

                return result;
            }

            INamedTypeSymbol TypeOrDefault(string metadataName)
            {
                var result = compilation.GetTypeByMetadataName(metadataName);
                return result;
            }
        }

        public INamedTypeSymbol Action_2 { get; private set; }
        public INamedTypeSymbol Byte { get; private set; }
        public INamedTypeSymbol ITypeManifestProvider { get; private set; }
        public INamedTypeSymbol Field { get; private set; }
        public INamedTypeSymbol WireType { get; private set; }
        public INamedTypeSymbol DeepCopier_1 { get; private set; }
        public INamedTypeSymbol FieldCodec_1 { get; private set; }
        public INamedTypeSymbol FieldCodec { get; private set; }
        public INamedTypeSymbol Func_2 { get; private set; }
        public INamedTypeSymbol GenerateMethodSerializersAttribute { get; private set; }
        public INamedTypeSymbol GenerateSerializerAttribute { get; private set; }
        public INamedTypeSymbol IActivator_1 { get; private set; }
        public INamedTypeSymbol IBufferWriter { get; private set; }
        public INamedTypeSymbol IInvokable { get; private set; }
        public INamedTypeSymbol Int32 { get; private set; }
        public INamedTypeSymbol UInt32 { get; private set; }
        public INamedTypeSymbol InvalidOperationException { get; private set; }
        public INamedTypeSymbol InvokablePool { get; private set; }
        public INamedTypeSymbol IResponseCompletionSource { get; private set; }
        public INamedTypeSymbol ITargetHolder { get; private set; }
        public INamedTypeSymbol TypeManifestProviderAttribute { get; private set; }
        public INamedTypeSymbol NonSerializedAttribute { get; private set; }
        public INamedTypeSymbol Object { get; private set; }
        public INamedTypeSymbol ObsoleteAttribute { get; private set; }
        public INamedTypeSymbol BaseCodec_1 { get; private set; }
        public INamedTypeSymbol BaseCopier_1 { get; private set; }
        public INamedTypeSymbol Reader { get; private set; }
        public INamedTypeSymbol Request { get; private set; }
        public INamedTypeSymbol Request_1 { get; private set; }
        public INamedTypeSymbol ResponseCompletionSourcePool { get; private set; }
        public INamedTypeSymbol TypeManifestOptions { get; private set; }
        public INamedTypeSymbol SerializerSession { get; private set; }
        public INamedTypeSymbol Task { get; private set; }
        public INamedTypeSymbol Task_1 { get; private set; }
        public INamedTypeSymbol TaskRequest { get; private set; }
        public INamedTypeSymbol TaskRequest_1 { get; private set; }
        public INamedTypeSymbol Type { get; private set; }
        public INamedTypeSymbol MethodInfo { get; private set; }
        public INamedTypeSymbol ICodecProvider { get; private set; }
        public INamedTypeSymbol ValueSerializer { get; private set; }
        public INamedTypeSymbol ValueTask { get; private set; }
        public INamedTypeSymbol ValueTask_1 { get; private set; }
        public INamedTypeSymbol ValueTypeSetter_2 { get; private set; }
        public INamedTypeSymbol Void { get; private set; }
        public INamedTypeSymbol Writer { get; private set; }
        public List<INamedTypeSymbol> IdAttributeTypes { get; private set; }
        public INamedTypeSymbol WellKnownAliasAttribute { get; private set; }
        public INamedTypeSymbol WellKnownIdAttribute { get; private set; }
        public List<WellKnownCodecDescription> StaticCodecs { get; private set; }
        public List<WellKnownCodecDescription> WellKnownCodecs { get; private set; }
        public List<WellKnownCopierDescription> StaticCopiers { get; private set; }
        public List<WellKnownCopierDescription> WellKnownCopiers { get; private set; }
        public INamedTypeSymbol RegisterCopierAttribute { get; private set; }
        public INamedTypeSymbol RegisterSerializerAttribute { get; private set; }
        public INamedTypeSymbol RegisterActivatorAttribute { get; private set; }
        public INamedTypeSymbol UseActivatorAttribute { get; private set; }
        public INamedTypeSymbol SuppressReferenceTrackingAttribute { get; private set; }
        public INamedTypeSymbol OmitDefaultMemberValuesAttribute { get; private set; }
        public INamedTypeSymbol CopyContext { get; private set; }
        public Compilation Compilation { get; private set; }
        public List<ITypeSymbol> ImmutableTypes { get; private set; }
        public INamedTypeSymbol TimeSpan { get; private set; }
        public INamedTypeSymbol DateTimeOffset { get; private set; }
        public INamedTypeSymbol Guid { get; private set; }
        public INamedTypeSymbol IPAddress { get; private set; }
        public INamedTypeSymbol IPEndPoint { get; private set; }
        public INamedTypeSymbol CancellationToken { get; private set; }
        public INamedTypeSymbol[] TupleTypes { get; private set; }
        public INamedTypeSymbol ValueTuple { get; private set; }
        public List<INamedTypeSymbol> ImmutableAttributes { get; private set; }
        public INamedTypeSymbol Exception { get; private set; }
        public INamedTypeSymbol VoidRequest { get; private set; }
        public INamedTypeSymbol ApplicationPartAttribute { get; private set; }
        public INamedTypeSymbol InvokeMethodNameAttribute { get; private set; }
        public INamedTypeSymbol InvokableCustomInitializerAttribute { get; private set; }
        public INamedTypeSymbol InvokableBaseTypeAttribute { get; private set; }
        public INamedTypeSymbol DefaultInvokableBaseTypeAttribute { get; private set; }
        public INamedTypeSymbol GenerateCodeForDeclaringAssemblyAttribute { get; private set; }
        public INamedTypeSymbol SerializationCallbacksAttribute { get; private set; }
        public INamedTypeSymbol DefaultInvokeMethodNameAttribute { get; private set; }
        public INamedTypeSymbol GeneratedActivatorConstructorAttribute { get; private set; }
        public INamedTypeSymbol IDisposable { get; private set; }
        public INamedTypeSymbol FSharpCompilationMappingAttributeOrDefault { get; private set; }
        public INamedTypeSymbol FSharpSourceConstructFlagsOrDefault { get; private set; }
        public INamedTypeSymbol FormatterServices { get; private set; }

#pragma warning disable RS1024 // Compare symbols correctly
        private readonly ConcurrentDictionary<ITypeSymbol, bool> _shallowCopyableTypes = new(SymbolEqualityComparer.Default);
#pragma warning restore RS1024 // Compare symbols correctly

        public bool IsShallowCopyable(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Char:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Decimal:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_String:
                case SpecialType.System_DateTime:
                    return true;
            }

            if (SymbolEqualityComparer.Default.Equals(TimeSpan, type)
                || SymbolEqualityComparer.Default.Equals(IPAddress, type)
                || SymbolEqualityComparer.Default.Equals(IPEndPoint, type)
                || SymbolEqualityComparer.Default.Equals(CancellationToken, type)
                || SymbolEqualityComparer.Default.Equals(Type, type))
            {
                return true;
            }

            if (_shallowCopyableTypes.TryGetValue(type, out var result))
            {
                return result;
            }

            foreach (var attr in ImmutableAttributes)
            {
                if (type.HasAttribute(attr))
                {
                    return _shallowCopyableTypes[type] = true;
                }
            }

            if (type.HasBaseType(Exception))
            {
                return _shallowCopyableTypes[type] = true;
            }

            if (!(type is INamedTypeSymbol namedType))
            {
                return _shallowCopyableTypes[type] = false;
            }

            if (namedType.IsTupleType)
            {
                return _shallowCopyableTypes[type] = namedType.TupleElements.All(f => IsShallowCopyable(f.Type));
            }
            else if (namedType.IsGenericType)
            {
                var def = namedType.ConstructedFrom;
                if (def.SpecialType == SpecialType.System_Nullable_T)
                {
                    return _shallowCopyableTypes[type] = IsShallowCopyable(namedType.TypeArguments.Single());
                }

                if (TupleTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, def)))
                {
                    return _shallowCopyableTypes[type] = namedType.TypeArguments.All(IsShallowCopyable);
                }
            }
            else
            {
                if (type.TypeKind == TypeKind.Enum)
                {
                    return _shallowCopyableTypes[type] = true;
                }

                if (type.TypeKind == TypeKind.Struct && !namedType.IsUnboundGenericType)
                {
                    return _shallowCopyableTypes[type] = IsValueTypeFieldsShallowCopyable(type);
                }
            }

            return _shallowCopyableTypes[type] = false;
        }

        private bool IsValueTypeFieldsShallowCopyable(ITypeSymbol type)
        {
            foreach (var field in type.GetDeclaredInstanceMembers<IFieldSymbol>())
            {
                if (field.Type is not INamedTypeSymbol fieldType)
                {
                    return false;
                }

                if (SymbolEqualityComparer.Default.Equals(type, fieldType))
                {
                    return false;
                }

                if (!IsShallowCopyable(fieldType))
                {
                    return false;
                }
            }

            return true;
        }
    }
}