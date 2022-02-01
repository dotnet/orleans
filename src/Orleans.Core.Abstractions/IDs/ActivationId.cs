using System;
using System.Buffers.Binary;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Uniquely identifies a grain activation.
    /// </summary>
    [Serializable, Immutable]
    [GenerateSerializer]
    public readonly struct ActivationId : IEquatable<ActivationId>
    {
        /// <summary>
        /// The default instance.
        /// </summary>
        public static readonly ActivationId Zero = GetActivationId(Guid.Empty);

        [DataMember(Order = 0)]
        [Id(0)]
        internal readonly Guid Key;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivationId"/> struct.
        /// </summary>
        /// <param name="key">The activation id.</param>
        public ActivationId(Guid key) => Key = key;

        /// <summary>
        /// Gets a value indicating whether the instance is the default instance.
        /// </summary>
        public bool IsDefault => Equals(Zero);

        /// <summary>
        /// Returns a new, random activation id.
        /// </summary>
        /// <returns>A new, random activation id.</returns>
        public static ActivationId NewId() => GetActivationId(Guid.NewGuid());

        /// <summary>
        /// Returns an activation id which has been computed deterministically and reproducibly from the provided grain id.
        /// </summary>
        /// <param name="grain">The grain id.</param>
        /// <returns>An activation id which has been computed deterministically and reproducibly from the provided grain id.</returns>
        public static ActivationId GetDeterministic(GrainId grain)
        {
            Span<byte> temp = stackalloc byte[16];
            var a = (ulong)grain.Type.GetUniformHashCode();
            var b = (ulong)grain.Key.GetUniformHashCode();
            BinaryPrimitives.WriteUInt64LittleEndian(temp, a);
            BinaryPrimitives.WriteUInt64LittleEndian(temp[8..], b);
            var key = new Guid(temp);
            return new ActivationId(key);
        }

        /// <summary>
        /// Gets an activation id representing the provided key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>An activation id representing the provided key.</returns>
        internal static ActivationId GetActivationId(Guid key) => new(key);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is ActivationId other && Key.Equals(other.Key);

        /// <inheritdoc />
        public bool Equals(ActivationId other) => Key.Equals(other.Key);

        /// <inheritdoc />
        public override int GetHashCode() => Key.GetHashCode();

        /// <inheritdoc />
        public override string ToString() => $"@{Key:N}";

        /// <summary>
        /// Returns a string representation of this activation id which can be parsed by <see cref="FromParsableString"/>.
        /// </summary>
        /// <returns>A string representation of this activation id which can be parsed by <see cref="FromParsableString"/>.</returns>
        public string ToParsableString() => ToString();

        /// <summary>
        /// Parses a string representation of an activation id which was created using <see cref="ToParsableString"/>.
        /// </summary>
        /// <param name="activationId">The string representation of the activation id.</param>
        /// <returns>The activation id.</returns>
        public static ActivationId FromParsableString(string activationId) => GetActivationId(Guid.Parse(activationId.Remove(0, 1)));

        /// <summary>
        /// Compares the provided operands for equality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are equal, otherwise <see langword="false"/>.</returns>
        public static bool operator ==(ActivationId left, ActivationId right) => left.Equals(right);

        /// <summary>
        /// Compares the provided operands for inequality.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns><see langword="true"/> if the provided values are not equal, otherwise <see langword="false"/>.</returns>
        public static bool operator !=(ActivationId left, ActivationId right) => !(left == right);
    }
}
