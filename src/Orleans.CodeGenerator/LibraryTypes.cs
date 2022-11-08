using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator
{
    internal sealed class LibraryTypes
    {
        private LibraryTypes() { }

        public static LibraryTypes FromCompilation(Compilation compilation, CodeGeneratorOptions options)
        {
            return new LibraryTypes
            {
                Compilation = compilation,
                ApplicationPartAttribute = Type("Orleans.ApplicationPartAttribute"),
                Action_2 = Type("System.Action`2"),
                ITypeManifestProvider = Type("Orleans.Serialization.Configuration.ITypeManifestProvider"),
                Field = Type("Orleans.Serialization.WireProtocol.Field"),
                FieldCodec_1 = Type("Orleans.Serialization.Codecs.IFieldCodec`1"),
                AbstractTypeSerializer = Type("Orleans.Serialization.Serializers.AbstractTypeSerializer`1"),
                DeepCopier_1 = Type("Orleans.Serialization.Cloning.IDeepCopier`1"),
                ShallowCopier = Type("Orleans.Serialization.Cloning.ShallowCopier`1"),
                CompoundTypeAliasAttribute = Type("Orleans.CompoundTypeAliasAttribute"),
                CopyContext = Type("Orleans.Serialization.Cloning.CopyContext"),
                MethodInfo = Type("System.Reflection.MethodInfo"),
                Func_2 = Type("System.Func`2"),
                GenerateMethodSerializersAttribute = Type("Orleans.GenerateMethodSerializersAttribute"),
                GenerateSerializerAttribute = Type("Orleans.GenerateSerializerAttribute"),
                SerializationCallbacksAttribute = Type("Orleans.SerializationCallbacksAttribute"),
                IActivator_1 = Type("Orleans.Serialization.Activators.IActivator`1"),
                IBufferWriter = Type("System.Buffers.IBufferWriter`1"),
                IdAttributeTypes = options.IdAttributes.Select(Type).ToArray(),
                ConstructorAttributeTypes = options.ConstructorAttributes.Select(Type).ToArray(),
                AliasAttribute = Type("Orleans.AliasAttribute"),
                IInvokable = Type("Orleans.Serialization.Invocation.IInvokable"),
                InvokeMethodNameAttribute = Type("Orleans.InvokeMethodNameAttribute"),
                FormatterServices = Type("System.Runtime.Serialization.FormatterServices"),
                InvokableCustomInitializerAttribute = Type("Orleans.InvokableCustomInitializerAttribute"),
                DefaultInvokableBaseTypeAttribute = Type("Orleans.DefaultInvokableBaseTypeAttribute"),
                GenerateCodeForDeclaringAssemblyAttribute = Type("Orleans.GenerateCodeForDeclaringAssemblyAttribute"),
                InvokableBaseTypeAttribute = Type("Orleans.InvokableBaseTypeAttribute"),
                RegisterSerializerAttribute = Type("Orleans.RegisterSerializerAttribute"),
                GeneratedActivatorConstructorAttribute = Type("Orleans.GeneratedActivatorConstructorAttribute"),
                SerializerTransparentAttribute = Type("Orleans.SerializerTransparentAttribute"),
                RegisterActivatorAttribute = Type("Orleans.RegisterActivatorAttribute"),
                RegisterConverterAttribute = Type("Orleans.RegisterConverterAttribute"),
                RegisterCopierAttribute = Type("Orleans.RegisterCopierAttribute"),
                UseActivatorAttribute = Type("Orleans.UseActivatorAttribute"),
                SuppressReferenceTrackingAttribute = Type("Orleans.SuppressReferenceTrackingAttribute"),
                OmitDefaultMemberValuesAttribute = Type("Orleans.OmitDefaultMemberValuesAttribute"),
                ITargetHolder = Type("Orleans.Serialization.Invocation.ITargetHolder"),
                TypeManifestProviderAttribute = Type("Orleans.Serialization.Configuration.TypeManifestProviderAttribute"),
                NonSerializedAttribute = Type("System.NonSerializedAttribute"),
                ObsoleteAttribute = Type("System.ObsoleteAttribute"),
                BaseCodec_1 = Type("Orleans.Serialization.Serializers.IBaseCodec`1"),
                BaseCopier_1 = Type("Orleans.Serialization.Cloning.IBaseCopier`1"),
                ArrayCodec = Type("Orleans.Serialization.Codecs.ArrayCodec`1"),
                ArrayCopier = Type("Orleans.Serialization.Codecs.ArrayCopier`1"),
                Reader = Type("Orleans.Serialization.Buffers.Reader`1"),
                TypeManifestOptions = Type("Orleans.Serialization.Configuration.TypeManifestOptions"),
                Task = Type("System.Threading.Tasks.Task"),
                Task_1 = Type("System.Threading.Tasks.Task`1"),
                Type = Type("System.Type"),
                Uri = Type("System.Uri"),
                Int128 = Type("System.Int128"),
                UInt128 = Type("System.UInt128"),
                Half = Type("System.Half"),
                DateOnly = Type("System.DateOnly"),
                DateTimeOffset = Type("System.DateTimeOffset"),
                BitVector32 = Type("System.Collections.Specialized.BitVector32"),
                Guid = Type("System.Guid"),
                CompareInfo = Type("System.Globalization.CompareInfo"),
                CultureInfo = Type("System.Globalization.CultureInfo"),
                Version = Type("System.Version"),
                TimeOnly = Type("System.TimeOnly"),
                ICodecProvider = Type("Orleans.Serialization.Serializers.ICodecProvider"),
                ValueSerializer = Type("Orleans.Serialization.Serializers.IValueSerializer`1"),
                ValueTask = Type("System.Threading.Tasks.ValueTask"),
                ValueTask_1 = Type("System.Threading.Tasks.ValueTask`1"),
                ValueTypeGetter_2 = Type("Orleans.Serialization.Utilities.ValueTypeGetter`2"),
                ValueTypeSetter_2 = Type("Orleans.Serialization.Utilities.ValueTypeSetter`2"),
                Writer = Type("Orleans.Serialization.Buffers.Writer`1"),
                FSharpSourceConstructFlagsOrDefault = TypeOrDefault("Microsoft.FSharp.Core.SourceConstructFlags"),
                FSharpCompilationMappingAttributeOrDefault = TypeOrDefault("Microsoft.FSharp.Core.CompilationMappingAttribute"),
                StaticCodecs = new WellKnownCodecDescription[]
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
                    new(compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Byte), 1), Type("Orleans.Serialization.Codecs.ByteArrayCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Single), Type("Orleans.Serialization.Codecs.FloatCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Double), Type("Orleans.Serialization.Codecs.DoubleCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_Decimal), Type("Orleans.Serialization.Codecs.DecimalCodec")),
                    new(compilation.GetSpecialType(SpecialType.System_DateTime), Type("Orleans.Serialization.Codecs.DateTimeCodec")),
                    new(Type("System.TimeSpan"), Type("Orleans.Serialization.Codecs.TimeSpanCodec")),
                    new(Type("System.DateTimeOffset"), Type("Orleans.Serialization.Codecs.DateTimeOffsetCodec")),
                    new(Type("System.DateOnly"), Type("Orleans.Serialization.Codecs.DateOnlyCodec")),
                    new(Type("System.TimeOnly"), Type("Orleans.Serialization.Codecs.TimeOnlyCodec")),
                    new(Type("System.Guid"), Type("Orleans.Serialization.Codecs.GuidCodec")),
                    new(Type("System.Type"), Type("Orleans.Serialization.Codecs.TypeSerializerCodec")),
                    new(Type("System.ReadOnlyMemory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.ReadOnlyMemoryOfByteCodec")),
                    new(Type("System.Memory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.MemoryOfByteCodec")),
                    new(Type("System.Net.IPAddress"), Type("Orleans.Serialization.Codecs.IPAddressCodec")),
                    new(Type("System.Net.IPEndPoint"), Type("Orleans.Serialization.Codecs.IPEndPointCodec")),
                    new(Type("System.UInt128"), Type("Orleans.Serialization.Codecs.UInt128Codec")),
                    new(Type("System.Int128"), Type("Orleans.Serialization.Codecs.Int128Codec")),
                    new(Type("System.Half"), Type("Orleans.Serialization.Codecs.HalfCodec")),
                },
                WellKnownCodecs = new WellKnownCodecDescription[]
                {
                    new(Type("System.Exception"), Type("Orleans.Serialization.ExceptionCodec")),
                    new(Type("System.Collections.Generic.Dictionary`2"), Type("Orleans.Serialization.Codecs.DictionaryCodec`2")),
                    new(Type("System.Collections.Generic.List`1"), Type("Orleans.Serialization.Codecs.ListCodec`1")),
                    new(Type("System.Collections.Generic.HashSet`1"), Type("Orleans.Serialization.Codecs.HashSetCodec`1")),
                    new(compilation.GetSpecialType(SpecialType.System_Nullable_T), Type("Orleans.Serialization.Codecs.NullableCodec`1")),
                    new(Type("System.Uri"), Type("Orleans.Serialization.Codecs.UriCodec")),
                },
                StaticCopiers = new WellKnownCopierDescription[]
                {
                    new(compilation.GetSpecialType(SpecialType.System_Object), Type("Orleans.Serialization.Codecs.ObjectCopier")),
                    new(compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Byte), 1), Type("Orleans.Serialization.Codecs.ByteArrayCopier")),
                    new(Type("System.ReadOnlyMemory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.ReadOnlyMemoryOfByteCopier")),
                    new(Type("System.Memory`1").Construct(compilation.GetSpecialType(SpecialType.System_Byte)), Type("Orleans.Serialization.Codecs.MemoryOfByteCopier")),
                },
                WellKnownCopiers = new WellKnownCopierDescription[]
                {
                    new(Type("System.Exception"), Type("Orleans.Serialization.ExceptionCodec")),
                    new(Type("System.Collections.Generic.Dictionary`2"), Type("Orleans.Serialization.Codecs.DictionaryCopier`2")),
                    new(Type("System.Collections.Generic.List`1"), Type("Orleans.Serialization.Codecs.ListCopier`1")),
                    new(Type("System.Collections.Generic.HashSet`1"), Type("Orleans.Serialization.Codecs.HashSetCopier`1")),
                    new(compilation.GetSpecialType(SpecialType.System_Nullable_T), Type("Orleans.Serialization.Codecs.NullableCopier`1")),
                },
                Exception = Type("System.Exception"),
                ImmutableAttributes = options.ImmutableAttributes.Select(Type).ToArray(),
                TimeSpan = Type("System.TimeSpan"),
                IPAddress = Type("System.Net.IPAddress"),
                IPEndPoint = Type("System.Net.IPEndPoint"),
                CancellationToken = Type("System.Threading.CancellationToken"),
                ImmutableContainerTypes = new[]
                {
                    compilation.GetSpecialType(SpecialType.System_Nullable_T),
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
                    Type("System.Collections.Immutable.ImmutableArray`1"),
                    Type("System.Collections.Immutable.ImmutableDictionary`2"),
                    Type("System.Collections.Immutable.ImmutableHashSet`1"),
                    Type("System.Collections.Immutable.ImmutableList`1"),
                    Type("System.Collections.Immutable.ImmutableQueue`1"),
                    Type("System.Collections.Immutable.ImmutableSortedDictionary`2"),
                    Type("System.Collections.Immutable.ImmutableSortedSet`1"),
                    Type("System.Collections.Immutable.ImmutableStack`1"),
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
        public INamedTypeSymbol ITypeManifestProvider { get; private set; }
        public INamedTypeSymbol Field { get; private set; }
        public INamedTypeSymbol DeepCopier_1 { get; private set; }
        public INamedTypeSymbol ShallowCopier { get; private set; }
        public INamedTypeSymbol FieldCodec_1 { get; private set; }
        public INamedTypeSymbol AbstractTypeSerializer { get; private set; }
        public INamedTypeSymbol Func_2 { get; private set; }
        public INamedTypeSymbol CompoundTypeAliasAttribute { get; private set; }
        public INamedTypeSymbol GenerateMethodSerializersAttribute { get; private set; }
        public INamedTypeSymbol GenerateSerializerAttribute { get; private set; }
        public INamedTypeSymbol IActivator_1 { get; private set; }
        public INamedTypeSymbol IBufferWriter { get; private set; }
        public INamedTypeSymbol IInvokable { get; private set; }
        public INamedTypeSymbol ITargetHolder { get; private set; }
        public INamedTypeSymbol TypeManifestProviderAttribute { get; private set; }
        public INamedTypeSymbol NonSerializedAttribute { get; private set; }
        public INamedTypeSymbol ObsoleteAttribute { get; private set; }
        public INamedTypeSymbol BaseCodec_1 { get; private set; }
        public INamedTypeSymbol BaseCopier_1 { get; private set; }
        public INamedTypeSymbol ArrayCodec { get; private set; }
        public INamedTypeSymbol ArrayCopier { get; private set; }
        public INamedTypeSymbol Reader { get; private set; }
        public INamedTypeSymbol TypeManifestOptions { get; private set; }
        public INamedTypeSymbol Task { get; private set; }
        public INamedTypeSymbol Task_1 { get; private set; }
        public INamedTypeSymbol Type { get; private set; }
        private INamedTypeSymbol Uri;
        private INamedTypeSymbol DateOnly;
        private INamedTypeSymbol DateTimeOffset;
        private INamedTypeSymbol TimeOnly;
        public INamedTypeSymbol MethodInfo { get; private set; }
        public INamedTypeSymbol ICodecProvider { get; private set; }
        public INamedTypeSymbol ValueSerializer { get; private set; }
        public INamedTypeSymbol ValueTask { get; private set; }
        public INamedTypeSymbol ValueTask_1 { get; private set; }
        public INamedTypeSymbol ValueTypeGetter_2 { get; private set; }
        public INamedTypeSymbol ValueTypeSetter_2 { get; private set; }
        public INamedTypeSymbol Writer { get; private set; }
        public INamedTypeSymbol[] IdAttributeTypes { get; private set; }
        public INamedTypeSymbol[] ConstructorAttributeTypes { get; private set; }
        public INamedTypeSymbol AliasAttribute { get; private set; }
        public WellKnownCodecDescription[] StaticCodecs { get; private set; }
        public WellKnownCodecDescription[] WellKnownCodecs { get; private set; }
        public WellKnownCopierDescription[] StaticCopiers { get; private set; }
        public WellKnownCopierDescription[] WellKnownCopiers { get; private set; }
        public INamedTypeSymbol RegisterCopierAttribute { get; private set; }
        public INamedTypeSymbol RegisterSerializerAttribute { get; private set; }
        public INamedTypeSymbol RegisterConverterAttribute { get; private set; }
        public INamedTypeSymbol RegisterActivatorAttribute { get; private set; }
        public INamedTypeSymbol UseActivatorAttribute { get; private set; }
        public INamedTypeSymbol SuppressReferenceTrackingAttribute { get; private set; }
        public INamedTypeSymbol OmitDefaultMemberValuesAttribute { get; private set; }
        public INamedTypeSymbol CopyContext { get; private set; }
        public Compilation Compilation { get; private set; }
        private INamedTypeSymbol TimeSpan;
        private INamedTypeSymbol IPAddress;
        private INamedTypeSymbol IPEndPoint;
        private INamedTypeSymbol CancellationToken;
        private INamedTypeSymbol[] ImmutableContainerTypes;
        private INamedTypeSymbol Guid;
        private INamedTypeSymbol BitVector32;
        private INamedTypeSymbol CompareInfo;
        private INamedTypeSymbol CultureInfo;
        private INamedTypeSymbol Version;
        private INamedTypeSymbol Int128;
        private INamedTypeSymbol UInt128;
        private INamedTypeSymbol Half;
        private INamedTypeSymbol[] _regularShallowCopyableTypes;
        private INamedTypeSymbol[] RegularShallowCopyableType => _regularShallowCopyableTypes ??= new[]
        {
            TimeSpan,
            DateOnly,
            TimeOnly,
            DateTimeOffset,
            Guid,
            BitVector32,
            CompareInfo,
            CultureInfo,
            Version,
            IPAddress,
            IPEndPoint,
            CancellationToken,
            Type,
            Uri,
            UInt128,
            Int128,
            Half
        };

        public INamedTypeSymbol[] ImmutableAttributes { get; private set; }
        public INamedTypeSymbol Exception { get; private set; }
        public INamedTypeSymbol ApplicationPartAttribute { get; private set; }
        public INamedTypeSymbol InvokeMethodNameAttribute { get; private set; }
        public INamedTypeSymbol InvokableCustomInitializerAttribute { get; private set; }
        public INamedTypeSymbol InvokableBaseTypeAttribute { get; private set; }
        public INamedTypeSymbol DefaultInvokableBaseTypeAttribute { get; private set; }
        public INamedTypeSymbol GenerateCodeForDeclaringAssemblyAttribute { get; private set; }
        public INamedTypeSymbol SerializationCallbacksAttribute { get; private set; }
        public INamedTypeSymbol GeneratedActivatorConstructorAttribute { get; private set; }
        public INamedTypeSymbol SerializerTransparentAttribute { get; private set; }
        public INamedTypeSymbol FSharpCompilationMappingAttributeOrDefault { get; private set; }
        public INamedTypeSymbol FSharpSourceConstructFlagsOrDefault { get; private set; }
        public INamedTypeSymbol FormatterServices { get; private set; }

        private readonly ConcurrentDictionary<ITypeSymbol, bool> _shallowCopyableTypes = new(SymbolEqualityComparer.Default);

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

            if (_shallowCopyableTypes.TryGetValue(type, out var result))
            {
                return result;
            }

            foreach (var shallowCopyable in RegularShallowCopyableType)
            {
                if (SymbolEqualityComparer.Default.Equals(shallowCopyable, type))
                {
                    return _shallowCopyableTypes[type] = true;
                }
            }

            if (type.IsSealed && type.HasAnyAttribute(ImmutableAttributes))
            {
                return _shallowCopyableTypes[type] = true;
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
                return _shallowCopyableTypes[type] = AreShallowCopyable(namedType.TupleElements);
            }
            else if (namedType.IsGenericType)
            {
                var def = namedType.ConstructedFrom;
                foreach (var t in ImmutableContainerTypes)
                {
                    if (SymbolEqualityComparer.Default.Equals(t, def))
                        return _shallowCopyableTypes[type] = AreShallowCopyable(namedType.TypeArguments);
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

        private bool AreShallowCopyable(ImmutableArray<ITypeSymbol> types)
        {
            foreach (var t in types)
                if (!IsShallowCopyable(t))
                    return false;

            return true;
        }

        private bool AreShallowCopyable(ImmutableArray<IFieldSymbol> fields)
        {
            foreach (var f in fields)
                if (!IsShallowCopyable(f.Type))
                    return false;

            return true;
        }
    }

    internal static class LibraryExtensions
    {
        public static WellKnownCodecDescription FindByUnderlyingType(this WellKnownCodecDescription[] values, ISymbol type)
        {
            foreach (var c in values)
                if (SymbolEqualityComparer.Default.Equals(c.UnderlyingType, type))
                    return c;

            return null;
        }

        public static WellKnownCopierDescription FindByUnderlyingType(this WellKnownCopierDescription[] values, ISymbol type)
        {
            foreach (var c in values)
                if (SymbolEqualityComparer.Default.Equals(c.UnderlyingType, type))
                    return c;

            return null;
        }
    }
}
