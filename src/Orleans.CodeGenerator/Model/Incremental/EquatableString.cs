using System;

namespace Orleans.CodeGenerator.Model.Incremental
{
    /// <summary>
    /// A string wrapper that implements <see cref="IEquatable{T}"/> with ordinal comparison,
    /// for use as elements in <see cref="EquatableArray{T}"/>.
    /// </summary>
    internal readonly struct EquatableString : IEquatable<EquatableString>, IComparable<EquatableString>
    {
        public EquatableString(string value) => Value = value;

        public string Value { get; }

        public bool Equals(EquatableString other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is EquatableString other && Equals(other);
        public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public int CompareTo(EquatableString other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
        public override string ToString() => Value;

        public static bool operator ==(EquatableString left, EquatableString right) => left.Equals(right);
        public static bool operator !=(EquatableString left, EquatableString right) => !left.Equals(right);

        public static implicit operator string(EquatableString s) => s.Value;
        public static implicit operator EquatableString(string s) => new EquatableString(s);
    }
}
