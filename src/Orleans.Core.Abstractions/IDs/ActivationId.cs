using System;
using System.Buffers.Binary;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    [Serializable, Immutable]
    [GenerateSerializer]
    public readonly struct ActivationId : IEquatable<ActivationId>
    {
        [DataMember(Order = 0)]
        [Id(0)]
        internal readonly Guid Key;

        public bool IsDefault => Equals(Zero);

        public static readonly ActivationId Zero = GetActivationId(Guid.Empty);

        private ActivationId(Guid key) => Key = key;

        public static ActivationId NewId() => GetActivationId(Guid.NewGuid());

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

        internal static ActivationId GetActivationId(Guid key) => new(key);

        public override bool Equals(object obj) => obj is ActivationId other && Key.Equals(other.Key);

        public bool Equals(ActivationId other) => Key.Equals(other.Key);

        public override int GetHashCode() => Key.GetHashCode();

        public override string ToString() => $"@{Key:N}";

        public string ToFullString() => ToString();

        public string ToParsableString() => ToFullString();

        public static ActivationId FromParsableString(string activationId) => GetActivationId(Guid.Parse(activationId.Remove(0, 1)));

        public static bool operator ==(ActivationId left, ActivationId right) => left.Equals(right);

        public static bool operator !=(ActivationId left, ActivationId right) => !(left == right);
    }
}
