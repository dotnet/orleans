using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime.Versions
{
    public readonly struct SemanticVersion : IEquatable<SemanticVersion>, IComparable<SemanticVersion>
    {
        public static readonly SemanticVersion Zero = new(0, 0, 0);

        private int Major { get; }
        private int Minor { get; }
        private int Patch { get; }
        private string? PreRelease { get; }

        private bool HasPreRelease => !string.IsNullOrEmpty(PreRelease);

        public SemanticVersion(int major, int minor, int patch, string? preRelease = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(major);
            ArgumentOutOfRangeException.ThrowIfNegative(minor);
            ArgumentOutOfRangeException.ThrowIfNegative(patch);

            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = string.IsNullOrWhiteSpace(preRelease) ? null : preRelease;
        }

        public static SemanticVersion Parse(string value)
        {
            if (!TryParse(value, out var result))
                throw new FormatException($"Invalid semantic version: '{value}'");
            return result;
        }

        public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var span = value.AsSpan().Trim();

            // Strip leading 'v' or 'V'
            if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
                span = span.Slice(1);

            // Split off pre-release: everything after first '-'
            string? preRelease = null;
            var hyphenIdx = span.IndexOf('-');
            if (hyphenIdx >= 0)
            {
                preRelease = span.Slice(hyphenIdx + 1).ToString();
                span = span.Slice(0, hyphenIdx);
            }

            // Strip build metadata (+...)
            var plusIdx = preRelease?.IndexOf('+') ?? span.IndexOf('+');
            if (preRelease != null && plusIdx >= 0)
            {
                preRelease = preRelease.Substring(0, plusIdx);
            }
            else if (plusIdx >= 0)
            {
                span = span.Slice(0, plusIdx);
            }

            // Parse Major.Minor.Patch
            var parts = span.ToString().Split('.');
            if (parts.Length < 2 || parts.Length > 3)
                return false;

            if (!int.TryParse(parts[0], out var major) || major < 0)
                return false;
            if (!int.TryParse(parts[1], out var minor) || minor < 0)
                return false;

            var patch = 0;
            if (parts.Length == 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
                return false;

            result = new SemanticVersion(major, minor, patch, preRelease);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            var cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;

            cmp = Minor.CompareTo(other.Minor);
            if (cmp != 0) return cmp;

            cmp = Patch.CompareTo(other.Patch);
            if (cmp != 0) return cmp;

            // No pre-release > has pre-release (1.0.0 > 1.0.0-alpha)
            if (!HasPreRelease && other.HasPreRelease) return 1;
            if (HasPreRelease && !other.HasPreRelease) return -1;
            if (!HasPreRelease && !other.HasPreRelease) return 0;

            return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);
        }

        public bool Equals(SemanticVersion other)
            => Major == other.Major
               && Minor == other.Minor
               && Patch == other.Patch
               && string.Equals(PreRelease, other.PreRelease, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

        public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);
        public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);
        public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
        public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
        public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
        public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

        public override string ToString() => HasPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
    }
}

