using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Runtime
{
    [Serializable]
    [GenerateSerializer]
    [JsonConverter(typeof(MembershipVersionConverter))]
    public struct MembershipVersion : IComparable<MembershipVersion>, IEquatable<MembershipVersion>
    {
        public MembershipVersion(long version)
        {
            this.Value = version;
        }

        [Id(1)]
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

    public class MembershipVersionConverter : JsonConverter<MembershipVersion>
    {
        public override MembershipVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, MembershipVersion value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }
}
