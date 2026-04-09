using Orleans.Runtime.Versions;

namespace Orleans.Runtime.Versions
{
    public readonly struct GrainInterfaceVersion : IEquatable<GrainInterfaceVersion>, IComparable<GrainInterfaceVersion>
    {
        public static readonly GrainInterfaceVersion Zero = new((ushort)0);

        private enum VersionKind : byte { Numeric, Semantic }

        private readonly VersionKind _kind;
        private readonly ushort _numeric;
        private readonly SemanticVersion _semantic;

        public GrainInterfaceVersion(ushort version)
        {
            _kind = VersionKind.Numeric;
            _numeric = version;
            _semantic = default;
        }

        public GrainInterfaceVersion(SemanticVersion version)
        {
            _kind = VersionKind.Semantic;
            _numeric = 0;
            _semantic = version;
        }

        /// <summary>Whether this is a legacy numeric version.</summary>
        public bool IsNumeric => _kind == VersionKind.Numeric;

        /// <summary>Whether this is a semantic version.</summary>
        public bool IsSemantic => _kind == VersionKind.Semantic;

        /// <summary>Gets the numeric value. Throws if this is a semantic version.</summary>
        public ushort NumericValue => IsNumeric ? _numeric : throw new InvalidOperationException("Not a numeric version.");

        /// <summary>Gets the semantic version value. Throws if this is a numeric version.</summary>
        public SemanticVersion SemanticValue => IsSemantic ? _semantic : throw new InvalidOperationException("Not a semantic version.");

        /// <summary>Whether this represents the default/zero version.</summary>
        public bool IsDefault => IsNumeric ? _numeric == 0 : _semantic.Equals(SemanticVersion.Zero);

        /// <summary>
        /// Parses a version string from grain interface properties.
        /// If the string is a valid ushort, creates a numeric version (backward compatible).
        /// Otherwise attempts to parse as SemVer.
        /// Falls back to Zero on null/empty.
        /// </summary>
        public static GrainInterfaceVersion Parse(string? versionString)
        {
            if (string.IsNullOrEmpty(versionString))
                return Zero;

            if (ushort.TryParse(versionString, out var numeric))
                return new GrainInterfaceVersion(numeric);

            if (SemanticVersion.TryParse(versionString, out var semver))
                return new GrainInterfaceVersion(semver);

            return Zero;
        }

        public static implicit operator GrainInterfaceVersion(ushort v) => new(v);
        public static implicit operator GrainInterfaceVersion(SemanticVersion v) => new(v);

        public int CompareTo(GrainInterfaceVersion other)
        {
            if (_kind != other._kind)
            {
                throw new InvalidOperationException(
                    $"Cannot compare {_kind} version with {other._kind} version. "
                    + "All versions for a grain interface must use the same versioning scheme.");
            }


            return _kind == VersionKind.Numeric
                ? _numeric.CompareTo(other._numeric)
                : _semantic.CompareTo(other._semantic);
        }

        public bool Equals(GrainInterfaceVersion other)
        {
            if (_kind != other._kind) return false;
            return _kind == VersionKind.Numeric
                ? _numeric == other._numeric
                : _semantic.Equals(other._semantic);
        }

        public override bool Equals(object? obj) => obj is GrainInterfaceVersion other && Equals(other);

        public override int GetHashCode() => _kind == VersionKind.Numeric
            ? HashCode.Combine(0, _numeric)
            : HashCode.Combine(1, _semantic);

        public static bool operator ==(GrainInterfaceVersion left, GrainInterfaceVersion right) => left.Equals(right);
        public static bool operator !=(GrainInterfaceVersion left, GrainInterfaceVersion right) => !left.Equals(right);
        public static bool operator <(GrainInterfaceVersion left, GrainInterfaceVersion right) => left.CompareTo(right) < 0;
        public static bool operator >(GrainInterfaceVersion left, GrainInterfaceVersion right) => left.CompareTo(right) > 0;
        public static bool operator <=(GrainInterfaceVersion left, GrainInterfaceVersion right) => left.CompareTo(right) <= 0;
        public static bool operator >=(GrainInterfaceVersion left, GrainInterfaceVersion right) => left.CompareTo(right) >= 0;

        public override string ToString() => _kind == VersionKind.Numeric ? _numeric.ToString() : _semantic.ToString();
    }

}

