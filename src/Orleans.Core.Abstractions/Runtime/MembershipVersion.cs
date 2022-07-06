using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies the version of a cluster membership configuration.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    [JsonConverter(typeof(MembershipVersionConverter))]
    public readonly struct MembershipVersion : IComparable<MembershipVersion>, IEquatable<MembershipVersion>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MembershipVersion"/> struct.
        /// </summary>
        /// <param name="version">The underlying version.</param>
        public MembershipVersion(long version)
        {
            this.Value = version;
        }

        /// <summary>
        /// Gets the version.
        /// </summary>
        [Id(1)]
        public long Value { get; init; }

        /// <summary>
        /// Gets the minimum possible version.
        /// </summary>
        public static MembershipVersion MinValue => new MembershipVersion(long.MinValue);

        /// <inheritdoc/>
        public int CompareTo(MembershipVersion other) => this.Value.CompareTo(other.Value);

        /// <inheritdoc/>
        public bool Equals(MembershipVersion other) => this.Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is MembershipVersion other && this.Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => this.Value.GetHashCode();

        /// <inheritdoc/>
        public override string ToString() => this.Value.ToString();

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(MembershipVersion left, MembershipVersion right) => left.Value == right.Value;

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(MembershipVersion left, MembershipVersion right) => left.Value != right.Value;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is greater than or equal to the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is greater than or equal to the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator >=(MembershipVersion left, MembershipVersion right) => left.Value >= right.Value;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is less than or equal to the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is less than or equal to the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator <=(MembershipVersion left, MembershipVersion right) => left.Value <= right.Value;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is greater than the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is greater than the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator >(MembershipVersion left, MembershipVersion right) => left.Value > right.Value;

        /// <summary>
        /// Compares the provided operands and returns <see langword="true"/> if the left operand is less than the right operand, otherwise <see langword="false"/>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the left operand is less than the right operand, otherwise <see langword="false"/>.</returns>
        public static bool operator <(MembershipVersion left, MembershipVersion right) => left.Value < right.Value;
    }

    /// <summary>
    /// Functionality for converting <see cref="MembershipVersion"/> instances to and from JSON.
    /// </summary>
    public sealed class MembershipVersionConverter : JsonConverter<MembershipVersion>
    {
        /// <inheritdoc />
        public override MembershipVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, MembershipVersion value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }
}
