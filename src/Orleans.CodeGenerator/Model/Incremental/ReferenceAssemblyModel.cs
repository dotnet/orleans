using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Describes a well-known type ID mapping.
    /// </summary>
    internal readonly struct WellKnownTypeIdModel : IEquatable<WellKnownTypeIdModel>
    {
        public WellKnownTypeIdModel(TypeRef type, uint id)
        {
            Type = type;
            Id = id;
        }

        public TypeRef Type { get; }
        public uint Id { get; }

        public bool Equals(WellKnownTypeIdModel other) => Type.Equals(other.Type) && Id == other.Id;
        public override bool Equals(object obj) => obj is WellKnownTypeIdModel other && Equals(other);
        public override int GetHashCode() { unchecked { return Type.GetHashCode() * 31 + (int)Id; } }

        public static bool operator ==(WellKnownTypeIdModel left, WellKnownTypeIdModel right) => left.Equals(right);
        public static bool operator !=(WellKnownTypeIdModel left, WellKnownTypeIdModel right) => !left.Equals(right);
    }

    /// <summary>
    /// Describes a type alias mapping.
    /// </summary>
    internal readonly struct TypeAliasModel : IEquatable<TypeAliasModel>
    {
        public TypeAliasModel(TypeRef type, string alias)
        {
            Type = type;
            Alias = alias;
        }

        public TypeRef Type { get; }
        public string Alias { get; }

        public bool Equals(TypeAliasModel other) =>
            Type.Equals(other.Type) && string.Equals(Alias, other.Alias, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is TypeAliasModel other && Equals(other);
        public override int GetHashCode() { unchecked { return Type.GetHashCode() * 31 + StringComparer.Ordinal.GetHashCode(Alias ?? string.Empty); } }

        public static bool operator ==(TypeAliasModel left, TypeAliasModel right) => left.Equals(right);
        public static bool operator !=(TypeAliasModel left, TypeAliasModel right) => !left.Equals(right);
    }

    /// <summary>
    /// A single component in a compound type alias path.
    /// </summary>
    internal readonly struct CompoundAliasComponentModel : IEquatable<CompoundAliasComponentModel>
    {
        private readonly string? _stringValue;
        private readonly TypeRef _typeValue;
        private readonly bool _isType;

        public CompoundAliasComponentModel(string stringValue)
        {
            _stringValue = stringValue;
            _typeValue = TypeRef.Empty;
            _isType = false;
        }

        public CompoundAliasComponentModel(TypeRef typeValue)
        {
            _stringValue = null;
            _typeValue = typeValue;
            _isType = true;
        }

        public bool IsString => !_isType && _stringValue is not null;
        public bool IsType => _isType;
        public string? StringValue => _stringValue;
        public TypeRef TypeValue => _typeValue;

        public bool Equals(CompoundAliasComponentModel other)
        {
            if (IsString != other.IsString || IsType != other.IsType) return false;
            if (IsString) return string.Equals(_stringValue, other._stringValue, StringComparison.Ordinal);
            if (IsType) return _typeValue.Equals(other._typeValue);
            return true;
        }

        public override bool Equals(object obj) => obj is CompoundAliasComponentModel other && Equals(other);

        public override int GetHashCode()
        {
            if (IsString) return StringComparer.Ordinal.GetHashCode(_stringValue ?? string.Empty);
            if (IsType) return _typeValue.GetHashCode();
            return 0;
        }

        public static bool operator ==(CompoundAliasComponentModel left, CompoundAliasComponentModel right) => left.Equals(right);
        public static bool operator !=(CompoundAliasComponentModel left, CompoundAliasComponentModel right) => !left.Equals(right);
    }

    /// <summary>
    /// Describes a compound type alias entry (a path of components mapping to a type).
    /// </summary>
    internal readonly struct CompoundTypeAliasModel : IEquatable<CompoundTypeAliasModel>
    {
        public CompoundTypeAliasModel(ImmutableArray<CompoundAliasComponentModel> components, TypeRef targetType)
        {
            Components = ImmutableArrayValueComparer.Normalize(components);
            TargetType = targetType;
        }

        public ImmutableArray<CompoundAliasComponentModel> Components { get; }
        public TypeRef TargetType { get; }

        public bool Equals(CompoundTypeAliasModel other) =>
            ImmutableArrayValueComparer.Equals(Components, other.Components) && TargetType.Equals(other.TargetType);

        public override bool Equals(object obj) => obj is CompoundTypeAliasModel other && Equals(other);
        public override int GetHashCode() { unchecked { return ImmutableArrayValueComparer.GetHashCode(Components) * 31 + TargetType.GetHashCode(); } }

        public static bool operator ==(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => left.Equals(right);
        public static bool operator !=(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => !left.Equals(right);
    }

    /// <summary>
    /// Describes an interface implementation (a concrete type implementing an invokable interface).
    /// </summary>
    internal readonly struct InterfaceImplementationModel : IEquatable<InterfaceImplementationModel>
    {
        public InterfaceImplementationModel(TypeRef implementationType, SourceLocationModel sourceLocation = default)
        {
            ImplementationType = implementationType;
            SourceLocation = sourceLocation;
        }

        public TypeRef ImplementationType { get; }
        public SourceLocationModel SourceLocation { get; }

        public bool Equals(InterfaceImplementationModel other)
            => ImplementationType.Equals(other.ImplementationType)
                && SourceLocation.Equals(other.SourceLocation);

        public override bool Equals(object obj) => obj is InterfaceImplementationModel other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                return ImplementationType.GetHashCode() * 31 + SourceLocation.GetHashCode();
            }
        }

        public static bool operator ==(InterfaceImplementationModel left, InterfaceImplementationModel right) => left.Equals(right);
        public static bool operator !=(InterfaceImplementationModel left, InterfaceImplementationModel right) => !left.Equals(right);
    }

    /// <summary>
    /// Aggregated data extracted from referenced assemblies via <c>[GenerateCodeForDeclaringAssembly]</c>
    /// and <c>[ApplicationPart]</c> attributes. This model is produced by a <c>CompilationProvider</c>-based
    /// pipeline and cached via structural equality.
    /// </summary>
    internal sealed class ReferenceAssemblyModel : IEquatable<ReferenceAssemblyModel>
    {
        public ReferenceAssemblyModel(
            string assemblyName,
            ImmutableArray<string> applicationParts,
            ImmutableArray<WellKnownTypeIdModel> wellKnownTypeIds,
            ImmutableArray<TypeAliasModel> typeAliases,
            ImmutableArray<CompoundTypeAliasModel> compoundTypeAliases,
            ImmutableArray<SerializableTypeModel> referencedSerializableTypes,
            ImmutableArray<ProxyInterfaceModel> referencedProxyInterfaces,
            ImmutableArray<RegisteredCodecModel> registeredCodecs,
            ImmutableArray<InterfaceImplementationModel> interfaceImplementations)
        {
            AssemblyName = assemblyName;
            ApplicationParts = ImmutableArrayValueComparer.Normalize(applicationParts);
            WellKnownTypeIds = ImmutableArrayValueComparer.Normalize(wellKnownTypeIds);
            TypeAliases = ImmutableArrayValueComparer.Normalize(typeAliases);
            CompoundTypeAliases = ImmutableArrayValueComparer.Normalize(compoundTypeAliases);
            ReferencedSerializableTypes = ImmutableArrayValueComparer.Normalize(referencedSerializableTypes);
            ReferencedProxyInterfaces = ImmutableArrayValueComparer.Normalize(referencedProxyInterfaces);
            RegisteredCodecs = ImmutableArrayValueComparer.Normalize(registeredCodecs);
            InterfaceImplementations = ImmutableArrayValueComparer.Normalize(interfaceImplementations);
        }

        public string AssemblyName { get; }
        public ImmutableArray<string> ApplicationParts { get; }
        public ImmutableArray<WellKnownTypeIdModel> WellKnownTypeIds { get; }
        public ImmutableArray<TypeAliasModel> TypeAliases { get; }
        public ImmutableArray<CompoundTypeAliasModel> CompoundTypeAliases { get; }
        public ImmutableArray<SerializableTypeModel> ReferencedSerializableTypes { get; }
        public ImmutableArray<ProxyInterfaceModel> ReferencedProxyInterfaces { get; }
        public ImmutableArray<RegisteredCodecModel> RegisteredCodecs { get; }
        public ImmutableArray<InterfaceImplementationModel> InterfaceImplementations { get; }

        public bool Equals(ReferenceAssemblyModel other)
        {
            if (other is null)
            {
                return false;
            }

            return string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal)
                && ImmutableArrayValueComparer.Equals(ApplicationParts, other.ApplicationParts)
                && ImmutableArrayValueComparer.Equals(WellKnownTypeIds, other.WellKnownTypeIds)
                && ImmutableArrayValueComparer.Equals(TypeAliases, other.TypeAliases)
                && ImmutableArrayValueComparer.Equals(CompoundTypeAliases, other.CompoundTypeAliases)
                && ImmutableArrayValueComparer.Equals(ReferencedSerializableTypes, other.ReferencedSerializableTypes)
                && ImmutableArrayValueComparer.Equals(ReferencedProxyInterfaces, other.ReferencedProxyInterfaces)
                && ImmutableArrayValueComparer.Equals(RegisteredCodecs, other.RegisteredCodecs)
                && ImmutableArrayValueComparer.Equals(InterfaceImplementations, other.InterfaceImplementations);
        }

        public override bool Equals(object obj) => obj is ReferenceAssemblyModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(ApplicationParts);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(WellKnownTypeIds);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(TypeAliases);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(CompoundTypeAliases);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(ReferencedSerializableTypes);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(ReferencedProxyInterfaces);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(RegisteredCodecs);
                hash = hash * 31 + ImmutableArrayValueComparer.GetHashCode(InterfaceImplementations);
                return hash;
            }
        }
    }
}
