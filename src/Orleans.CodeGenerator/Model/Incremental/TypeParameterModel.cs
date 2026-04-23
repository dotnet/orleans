using System;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// Describes a type parameter in a serializable or proxy type.
    /// </summary>
    internal readonly struct TypeParameterModel : IEquatable<TypeParameterModel>
    {
        public TypeParameterModel(string name, string originalName, int ordinal)
        {
            Name = name;
            OriginalName = originalName;
            Ordinal = ordinal;
        }

        public string Name { get; }
        public string OriginalName { get; }
        public int Ordinal { get; }

        public bool Equals(TypeParameterModel other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(OriginalName, other.OriginalName, StringComparison.Ordinal)
            && Ordinal == other.Ordinal;

        public override bool Equals(object obj) => obj is TypeParameterModel other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(OriginalName ?? string.Empty);
                hash = hash * 31 + Ordinal;
                return hash;
            }
        }

        public static bool operator ==(TypeParameterModel left, TypeParameterModel right) => left.Equals(right);
        public static bool operator !=(TypeParameterModel left, TypeParameterModel right) => !left.Equals(right);
    }
}
