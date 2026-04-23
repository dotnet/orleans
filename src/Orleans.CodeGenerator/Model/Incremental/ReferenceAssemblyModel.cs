using System;

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
        private readonly string _stringValue;
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
        public string StringValue => _stringValue;
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
        public CompoundTypeAliasModel(EquatableArray<CompoundAliasComponentModel> components, TypeRef targetType)
        {
            Components = components;
            TargetType = targetType;
        }

        public EquatableArray<CompoundAliasComponentModel> Components { get; }
        public TypeRef TargetType { get; }

        public bool Equals(CompoundTypeAliasModel other) =>
            Components.Equals(other.Components) && TargetType.Equals(other.TargetType);

        public override bool Equals(object obj) => obj is CompoundTypeAliasModel other && Equals(other);
        public override int GetHashCode() { unchecked { return Components.GetHashCode() * 31 + TargetType.GetHashCode(); } }

        public static bool operator ==(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => left.Equals(right);
        public static bool operator !=(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => !left.Equals(right);
    }

    /// <summary>
    /// Describes an interface implementation (a concrete type implementing an invokable interface).
    /// </summary>
    internal readonly struct InterfaceImplementationModel : IEquatable<InterfaceImplementationModel>
    {
        public InterfaceImplementationModel(TypeRef implementationType)
        {
            ImplementationType = implementationType;
        }

        public TypeRef ImplementationType { get; }

        public bool Equals(InterfaceImplementationModel other) => ImplementationType.Equals(other.ImplementationType);
        public override bool Equals(object obj) => obj is InterfaceImplementationModel other && Equals(other);
        public override int GetHashCode() => ImplementationType.GetHashCode();

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
            EquatableArray<EquatableString> applicationParts,
            EquatableArray<WellKnownTypeIdModel> wellKnownTypeIds,
            EquatableArray<TypeAliasModel> typeAliases,
            EquatableArray<CompoundTypeAliasModel> compoundTypeAliases,
            EquatableArray<SerializableTypeModel> referencedSerializableTypes,
            EquatableArray<ProxyInterfaceModel> referencedProxyInterfaces,
            EquatableArray<RegisteredCodecModel> registeredCodecs,
            EquatableArray<InterfaceImplementationModel> interfaceImplementations)
        {
            AssemblyName = assemblyName;
            ApplicationParts = applicationParts;
            WellKnownTypeIds = wellKnownTypeIds;
            TypeAliases = typeAliases;
            CompoundTypeAliases = compoundTypeAliases;
            ReferencedSerializableTypes = referencedSerializableTypes;
            ReferencedProxyInterfaces = referencedProxyInterfaces;
            RegisteredCodecs = registeredCodecs;
            InterfaceImplementations = interfaceImplementations;
        }

        public string AssemblyName { get; }
        public EquatableArray<EquatableString> ApplicationParts { get; }
        public EquatableArray<WellKnownTypeIdModel> WellKnownTypeIds { get; }
        public EquatableArray<TypeAliasModel> TypeAliases { get; }
        public EquatableArray<CompoundTypeAliasModel> CompoundTypeAliases { get; }
        public EquatableArray<SerializableTypeModel> ReferencedSerializableTypes { get; }
        public EquatableArray<ProxyInterfaceModel> ReferencedProxyInterfaces { get; }
        public EquatableArray<RegisteredCodecModel> RegisteredCodecs { get; }
        public EquatableArray<InterfaceImplementationModel> InterfaceImplementations { get; }

        public bool Equals(ReferenceAssemblyModel other)
        {
            if (other is null)
            {
                return false;
            }

            return string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal)
                && ApplicationParts.Equals(other.ApplicationParts)
                && WellKnownTypeIds.Equals(other.WellKnownTypeIds)
                && TypeAliases.Equals(other.TypeAliases)
                && CompoundTypeAliases.Equals(other.CompoundTypeAliases)
                && ReferencedSerializableTypes.Equals(other.ReferencedSerializableTypes)
                && ReferencedProxyInterfaces.Equals(other.ReferencedProxyInterfaces)
                && RegisteredCodecs.Equals(other.RegisteredCodecs)
                && InterfaceImplementations.Equals(other.InterfaceImplementations);
        }

        public override bool Equals(object obj) => obj is ReferenceAssemblyModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
                hash = hash * 31 + ApplicationParts.GetHashCode();
                hash = hash * 31 + WellKnownTypeIds.GetHashCode();
                hash = hash * 31 + TypeAliases.GetHashCode();
                hash = hash * 31 + CompoundTypeAliases.GetHashCode();
                hash = hash * 31 + ReferencedSerializableTypes.GetHashCode();
                hash = hash * 31 + ReferencedProxyInterfaces.GetHashCode();
                hash = hash * 31 + RegisteredCodecs.GetHashCode();
                hash = hash * 31 + InterfaceImplementations.GetHashCode();
                return hash;
            }
        }
    }
}
