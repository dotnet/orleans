using System;

namespace Orleans.Metadata
{
    /// <summary>
    /// Represents a version with two components, a major (most-significant) component, and a minor (least-significant) component.
    /// </summary>
    [Serializable]
    public readonly struct MajorMinorVersion : IComparable<MajorMinorVersion>, IEquatable<MajorMinorVersion>
    {
        private static readonly char[] VersionSeparator = new char[] { '.' };

        public MajorMinorVersion(long majorVersion, long minorVersion)
        {
            Major = majorVersion;
            Minor = minorVersion;
        }

        /// <summary>
        /// Gets the zero value.
        /// </summary>
        public static MajorMinorVersion Zero => new MajorMinorVersion(0, 0);

        /// <summary>
        /// Gets the most significant version component.
        /// </summary>
        public long Major { get; }

        /// <summary>
        /// Gets the least significant version component.
        /// </summary>
        public long Minor { get; }

        /// <summary>
        /// Compares this instance to another instance.
        /// </summary>
        public int CompareTo(MajorMinorVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;

            return Minor.CompareTo(other.Minor);
        }

        /// <summary>
        /// Returns <see langword="true"/> if this value is equal to the provided value.
        /// </summary>
        public bool Equals(MajorMinorVersion other) => Major == other.Major && Minor == other.Minor;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is MajorMinorVersion other && this.Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Major, Minor);

        /// <summary>
        /// Parses a <see cref="MajorMinorVersion"/>.
        /// </summary>
        public static MajorMinorVersion Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(value));

            var parts = value.Split(VersionSeparator, 2);
            return new MajorMinorVersion(long.Parse(parts[0]), long.Parse(parts[1]));
        }

        /// <inheritdoc />
        public override string ToString() => $"{Major}{VersionSeparator[0]}{Minor}";

        /// <inheritdoc />
        public static bool operator ==(MajorMinorVersion left, MajorMinorVersion right) => left.Equals(right);

        /// <inheritdoc />
        public static bool operator !=(MajorMinorVersion left, MajorMinorVersion right) => !left.Equals(right);

        /// <inheritdoc />
        public static bool operator >=(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) >= 0;

        /// <inheritdoc />
        public static bool operator <=(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) <= 0;

        /// <inheritdoc />
        public static bool operator >(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) > 0;

        /// <inheritdoc />
        public static bool operator <(MajorMinorVersion left, MajorMinorVersion right) => left.CompareTo(right) < 0;
    }
}
