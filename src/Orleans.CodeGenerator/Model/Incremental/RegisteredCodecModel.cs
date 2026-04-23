using System;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// The kind of manually-registered codec/provider.
    /// </summary>
    internal enum RegisteredCodecKind : byte
    {
        Serializer,
        Copier,
        Activator,
        Converter
    }

    /// <summary>
    /// Describes a type annotated with <c>[RegisterSerializer]</c>, <c>[RegisterCopier]</c>,
    /// <c>[RegisterActivator]</c>, or <c>[RegisterConverter]</c>.
    /// </summary>
    internal readonly struct RegisteredCodecModel : IEquatable<RegisteredCodecModel>
    {
        public RegisteredCodecModel(TypeRef type, RegisteredCodecKind kind)
        {
            Type = type;
            Kind = kind;
        }

        public TypeRef Type { get; }
        public RegisteredCodecKind Kind { get; }

        public bool Equals(RegisteredCodecModel other) => Type.Equals(other.Type) && Kind == other.Kind;
        public override bool Equals(object obj) => obj is RegisteredCodecModel other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { return Type.GetHashCode() * 31 + (int)Kind; }
        }

        public static bool operator ==(RegisteredCodecModel left, RegisteredCodecModel right) => left.Equals(right);
        public static bool operator !=(RegisteredCodecModel left, RegisteredCodecModel right) => !left.Equals(right);
    }
}
