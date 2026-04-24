using System;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Describes the kind of a serializable member.
    /// </summary>
    internal enum MemberKind : byte
    {
        Field,
        Property
    }

    /// <summary>
    /// Describes the accessibility strategy for getting/setting a member value during serialization.
    /// </summary>
    internal enum AccessStrategy : byte
    {
        /// <summary>
        /// The member can be accessed directly (public field or property with accessible getter/setter).
        /// </summary>
        Direct,

        /// <summary>
        /// The member requires a generated delegate-based accessor (FieldAccessor utility).
        /// </summary>
        GeneratedAccessor,

        /// <summary>
        /// The member requires an UnsafeAccessor-based accessor.
        /// </summary>
        UnsafeAccessor
    }

    /// <summary>
    /// Describes the strategy for constructing an instance of a serializable type during deserialization.
    /// </summary>
    internal enum ObjectCreationStrategy : byte
    {
        /// <summary>
        /// Use <c>default(T)</c> for value types.
        /// </summary>
        Default,

        /// <summary>
        /// Use <c>new T()</c> — type has an accessible parameterless constructor.
        /// </summary>
        NewExpression,

        /// <summary>
        /// Use <c>RuntimeHelpers.GetUninitializedObject(typeof(T))</c>.
        /// </summary>
        GetUninitializedObject
    }

    /// <summary>
    /// Describes a serializable field or property member in a <see cref="SerializableTypeModel"/>.
    /// Contains all data needed for serializer, copier, and activator generation without holding <c>ISymbol</c> references.
    /// </summary>
    internal sealed class MemberModel : IEquatable<MemberModel>
    {
        public MemberModel(
            uint fieldId,
            string name,
            TypeRef type,
            TypeRef containingType,
            string assemblyName,
            string typeNameIdentifier,
            bool isPrimaryConstructorParameter,
            bool isSerializable,
            bool isCopyable,
            MemberKind kind,
            AccessStrategy getterStrategy,
            AccessStrategy setterStrategy,
            bool isObsolete,
            bool hasImmutableAttribute,
            bool isShallowCopyable,
            bool isValueType,
            bool containingTypeIsValueType,
            string? backingPropertyName)
        {
            FieldId = fieldId;
            Name = name;
            Type = type;
            ContainingType = containingType;
            AssemblyName = assemblyName;
            TypeNameIdentifier = typeNameIdentifier;
            IsPrimaryConstructorParameter = isPrimaryConstructorParameter;
            IsSerializable = isSerializable;
            IsCopyable = isCopyable;
            Kind = kind;
            GetterStrategy = getterStrategy;
            SetterStrategy = setterStrategy;
            IsObsolete = isObsolete;
            HasImmutableAttribute = hasImmutableAttribute;
            IsShallowCopyable = isShallowCopyable;
            IsValueType = isValueType;
            ContainingTypeIsValueType = containingTypeIsValueType;
            BackingPropertyName = backingPropertyName;
        }

        public uint FieldId { get; }
        public string Name { get; }
        public TypeRef Type { get; }
        public TypeRef ContainingType { get; }
        public string AssemblyName { get; }
        public string TypeNameIdentifier { get; }
        public bool IsPrimaryConstructorParameter { get; }
        public bool IsSerializable { get; }
        public bool IsCopyable { get; }
        public MemberKind Kind { get; }
        public AccessStrategy GetterStrategy { get; }
        public AccessStrategy SetterStrategy { get; }
        public bool IsObsolete { get; }
        public bool HasImmutableAttribute { get; }
        public bool IsShallowCopyable { get; }
        public bool IsValueType { get; }
        public bool ContainingTypeIsValueType { get; }
        public string? BackingPropertyName { get; }

        public bool Equals(MemberModel other)
        {
            if (other is null)
            {
                return false;
            }

            return FieldId == other.FieldId
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && Type.Equals(other.Type)
                && ContainingType.Equals(other.ContainingType)
                && string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal)
                && string.Equals(TypeNameIdentifier, other.TypeNameIdentifier, StringComparison.Ordinal)
                && IsPrimaryConstructorParameter == other.IsPrimaryConstructorParameter
                && IsSerializable == other.IsSerializable
                && IsCopyable == other.IsCopyable
                && Kind == other.Kind
                && GetterStrategy == other.GetterStrategy
                && SetterStrategy == other.SetterStrategy
                && IsObsolete == other.IsObsolete
                && HasImmutableAttribute == other.HasImmutableAttribute
                && IsShallowCopyable == other.IsShallowCopyable
                && IsValueType == other.IsValueType
                && ContainingTypeIsValueType == other.ContainingTypeIsValueType
                && string.Equals(BackingPropertyName, other.BackingPropertyName, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is MemberModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)FieldId;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
                hash = hash * 31 + Type.GetHashCode();
                hash = hash * 31 + ContainingType.GetHashCode();
                hash = hash * 31 + (int)Kind;
                hash = hash * 31 + (int)GetterStrategy;
                hash = hash * 31 + (int)SetterStrategy;
                hash = hash * 31 + (IsPrimaryConstructorParameter ? 1 : 0);
                hash = hash * 31 + (IsSerializable ? 1 : 0);
                hash = hash * 31 + (IsCopyable ? 1 : 0);
                hash = hash * 31 + (IsObsolete ? 1 : 0);
                hash = hash * 31 + (HasImmutableAttribute ? 1 : 0);
                hash = hash * 31 + (IsShallowCopyable ? 1 : 0);
                hash = hash * 31 + (IsValueType ? 1 : 0);
                hash = hash * 31 + (ContainingTypeIsValueType ? 1 : 0);
                return hash;
            }
        }
    }
}
