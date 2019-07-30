using System;

namespace Orleans.Runtime
{
    [Serializable]
    public struct MembershipVersion : IComparable<MembershipVersion>, IEquatable<MembershipVersion>
    {
        private readonly long version;

        public MembershipVersion(long version)
        {
            this.version = version;
        }

        public static MembershipVersion MinValue => new MembershipVersion(long.MinValue);

        public int CompareTo(MembershipVersion other) => this.version.CompareTo(other.version);

        public bool Equals(MembershipVersion other) => this.version == other.version;

        public override bool Equals(object obj) => obj is MembershipVersion other && this.Equals(other);

        public override int GetHashCode() => this.version.GetHashCode();

        public override string ToString() => this.version.ToString();

        public static bool operator ==(MembershipVersion left, MembershipVersion right) => left.version == right.version;
        public static bool operator !=(MembershipVersion left, MembershipVersion right) => left.version != right.version;
        public static bool operator >=(MembershipVersion left, MembershipVersion right) => left.version >= right.version;
        public static bool operator <=(MembershipVersion left, MembershipVersion right) => left.version <= right.version;
        public static bool operator >(MembershipVersion left, MembershipVersion right) => left.version > right.version;
        public static bool operator <(MembershipVersion left, MembershipVersion right) => left.version < right.version;
    }
}
