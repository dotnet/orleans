using System;

namespace Orleans.Runtime
{
    [Serializable]
    public struct MembershipVersion : IComparable<MembershipVersion>, IEquatable<MembershipVersion>
    {
        public MembershipVersion(long version)
        {
            this.Value = version;
        }

        public long Value { get; private set; }

        public static MembershipVersion MinValue => new MembershipVersion(long.MinValue);

        public int CompareTo(MembershipVersion other) => this.Value.CompareTo(other.Value);

        public bool Equals(MembershipVersion other) => this.Value == other.Value;

        public override bool Equals(object obj) => obj is MembershipVersion other && this.Equals(other);

        public override int GetHashCode() => this.Value.GetHashCode();

        public override string ToString() => this.Value.ToString();

        public static bool operator ==(MembershipVersion left, MembershipVersion right) => left.Value == right.Value;
        public static bool operator !=(MembershipVersion left, MembershipVersion right) => left.Value != right.Value;
        public static bool operator >=(MembershipVersion left, MembershipVersion right) => left.Value >= right.Value;
        public static bool operator <=(MembershipVersion left, MembershipVersion right) => left.Value <= right.Value;
        public static bool operator >(MembershipVersion left, MembershipVersion right) => left.Value > right.Value;
        public static bool operator <(MembershipVersion left, MembershipVersion right) => left.Value < right.Value;
    }
}
