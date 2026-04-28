using System;
using System.Collections.Immutable;

namespace Orleans.CodeGenerator.Model
{
    /// <summary>
    /// Describes a well-known type ID mapping.
    /// </summary>
    internal readonly record struct WellKnownTypeIdModel(TypeRef Type, uint Id);

    /// <summary>
    /// Describes a type alias mapping.
    /// </summary>
    internal readonly record struct TypeAliasModel(TypeRef Type, string Alias);

    /// <summary>
    /// A single component in a compound type alias path.
    /// </summary>
    internal readonly record struct CompoundAliasComponentModel
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

    }

    /// <summary>
    /// Describes a compound type alias entry (a path of components mapping to a type).
    /// </summary>
    internal readonly struct CompoundTypeAliasModel : IEquatable<CompoundTypeAliasModel>
    {
        public CompoundTypeAliasModel(ImmutableArray<CompoundAliasComponentModel> components, TypeRef targetType)
        {
            Components = StructuralEquality.Normalize(components);
            TargetType = targetType;
        }

        public ImmutableArray<CompoundAliasComponentModel> Components { get; }
        public TypeRef TargetType { get; }

        public bool Equals(CompoundTypeAliasModel other) =>
            StructuralEquality.SequenceEqual(Components, other.Components) && TargetType.Equals(other.TargetType);

        public override bool Equals(object obj) => obj is CompoundTypeAliasModel other && Equals(other);
        public override int GetHashCode() { unchecked { return StructuralEquality.GetSequenceHashCode(Components) * 31 + TargetType.GetHashCode(); } }

        public static bool operator ==(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => left.Equals(right);
        public static bool operator !=(CompoundTypeAliasModel left, CompoundTypeAliasModel right) => !left.Equals(right);
    }

    /// <summary>
    /// Describes an interface implementation (a concrete type implementing an invokable interface).
    /// </summary>
    internal readonly record struct InterfaceImplementationModel
    {
        public InterfaceImplementationModel(TypeRef implementationType, SourceLocationModel sourceLocation = default)
        {
            ImplementationType = implementationType;
            SourceLocation = sourceLocation;
        }

        public TypeRef ImplementationType { get; }
        public SourceLocationModel SourceLocation { get; }
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
            ApplicationParts = StructuralEquality.Normalize(applicationParts);
            WellKnownTypeIds = StructuralEquality.Normalize(wellKnownTypeIds);
            TypeAliases = StructuralEquality.Normalize(typeAliases);
            CompoundTypeAliases = StructuralEquality.Normalize(compoundTypeAliases);
            ReferencedSerializableTypes = StructuralEquality.Normalize(referencedSerializableTypes);
            ReferencedProxyInterfaces = StructuralEquality.Normalize(referencedProxyInterfaces);
            RegisteredCodecs = StructuralEquality.Normalize(registeredCodecs);
            InterfaceImplementations = StructuralEquality.Normalize(interfaceImplementations);
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
                && StructuralEquality.SequenceEqual(ApplicationParts, other.ApplicationParts)
                && StructuralEquality.SequenceEqual(WellKnownTypeIds, other.WellKnownTypeIds)
                && StructuralEquality.SequenceEqual(TypeAliases, other.TypeAliases)
                && StructuralEquality.SequenceEqual(CompoundTypeAliases, other.CompoundTypeAliases)
                && StructuralEquality.SequenceEqual(ReferencedSerializableTypes, other.ReferencedSerializableTypes)
                && StructuralEquality.SequenceEqual(ReferencedProxyInterfaces, other.ReferencedProxyInterfaces)
                && StructuralEquality.SequenceEqual(RegisteredCodecs, other.RegisteredCodecs)
                && StructuralEquality.SequenceEqual(InterfaceImplementations, other.InterfaceImplementations);
        }

        public override bool Equals(object obj) => obj is ReferenceAssemblyModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(AssemblyName ?? string.Empty);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ApplicationParts);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(WellKnownTypeIds);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(TypeAliases);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(CompoundTypeAliases);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ReferencedSerializableTypes);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(ReferencedProxyInterfaces);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(RegisteredCodecs);
                hash = hash * 31 + StructuralEquality.GetSequenceHashCode(InterfaceImplementations);
                return hash;
            }
        }
    }
}
