using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct GrainType : IEquatable<GrainType>, IComparable<GrainType>, ISerializable
    {
        private readonly SpanId _value;
        
        public GrainType(byte[] value) => _value = new SpanId(value);

        public GrainType(byte[] value, int hashCode) => _value = new SpanId(value, hashCode);
        
        public GrainType(SerializationInfo info, StreamingContext context)
        {
            _value = new SpanId((byte[])info.GetValue("v", typeof(byte[])), info.GetInt32("h"));
        }

        public GrainType(SpanId id) => _value = id;

        public static GrainType Create(string value) => new GrainType(Encoding.UTF8.GetBytes(value));

        public static GrainType CreateForSystemTarget(string name) => Create(GrainTypePrefix.SystemTargetPrefix + name);

        public static explicit operator SpanId(GrainType kind) => kind._value;

        public static explicit operator GrainType(SpanId id) => new GrainType(id);

        public readonly bool IsDefault => _value.IsDefault;

        public readonly ReadOnlyMemory<byte> Value => _value.Value;

        public override readonly bool Equals(object obj) => obj is GrainType kind && this.Equals(kind);

        public readonly bool Equals(GrainType obj) => _value.Equals(obj._value);

        public override readonly int GetHashCode() => _value.GetHashCode();

        public static byte[] UnsafeGetArray(GrainType id) => SpanId.UnsafeGetArray(id._value);

        public static SpanId AsSpanId(GrainType id) => id._value;

        public readonly int CompareTo(GrainType other) => _value.CompareTo(other._value);

        public readonly void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", SpanId.UnsafeGetArray(_value));
            info.AddValue("h", _value.GetHashCode());
        }

        public override string ToString() => this.ToStringUtf8();

        public readonly string ToStringUtf8() => _value.ToStringUtf8();

        public sealed class Comparer : IEqualityComparer<GrainType>, IComparer<GrainType>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(GrainType x, GrainType y) => x.CompareTo(y);

            public bool Equals(GrainType x, GrainType y) => x.Equals(y);

            public int GetHashCode(GrainType obj) => obj.GetHashCode();
        }
    }
}
