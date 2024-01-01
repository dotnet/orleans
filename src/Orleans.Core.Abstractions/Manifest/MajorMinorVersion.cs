using System;

namespace Orleans.Metadata
{
    /// <summary>
    /// Represents a version with two components, a major (most-significant) component, and a minor (least-significant) component.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public readonly struct MajorMinorVersion : IComparable<MajorMinorVersion>, IEquatable<MajorMinorVersion>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MajorMinorVersion"/> struct.
        /// </summary>
        /// <param name="majorVersion">The major version component.</param>
        /// <param name="minorVersion">The minor version component.</param>
        public MajorMinorVersion(long majorVersion, long minorVersion)
        {
            Major = majorVersion;
            Minor = minorVersion;
        }

        /// <summary>
        /// Gets the zero value.
        /// </summary>
        public static MajorMinorVersion Zero => new(0, 0);

        /// <summary>
        /// Gets the minimum value.
        /// </summary>
        public static MajorMinorVersion MinValue => new(long.MinValue, long.MinValue);

        /// <summary>
        /// Gets the most significant version component.
        /// </summary>
        [Id(0)]
        public long Major { get; }

        /// <summary>
        /// Gets the least significant version component.
        /// </summary>
        [Id(1)]
        public long Minor { get; }

        /// <inheritdoc />
        public int CompareTo(MajorMinorVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;

            return Minor.CompareTo(other.Minor);
        }

        /// <inheritdoc />
        public bool Equals(MajorMinorVersion other) => Major == other.Major && Minor == other.Minor;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is MajorMinorVersion other && this.Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Major, Minor);

        /// <summary>
        /// Parses a <see cref="MajorMinorVersion"/>.
        /// </summary>
        /// <param name="value">
        /// The string representation.
        /// </param>
        /// <returns>
        /// The parsed <see cref="MajorMinorVersion"/> value.
        /// </returns>
        public static MajorMinorVersion Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(value));

            var i = value.IndexOf('.');
            if (i < 0) throw new ArgumentException(nameof(value));
            return new MajorMinorVersion(long.Parse(value[..i]), long.Parse(value[(i + 1)..]));
        }

        /// <inheritdoc />
        public override string ToString() => $"{Major}.{Minor}";

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(MajorMinorVersion left, MajorMinorVersion right) => left.Equals(right);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(MajorMinorVersion left, MajorMinorVersion right) => !left.Equals(right);

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is greater than or equal to the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is greater than or equal to the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator >=(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) >= 0;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is less than or equal to the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is less than or equal to the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator <=(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) <= 0;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is greater than the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is greater than the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator >(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) > 0;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is less than the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is less than the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator <(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) < 0;
    }
}
