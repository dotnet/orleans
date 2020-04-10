using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;

namespace Orleans.Runtime
{
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct SpanId : IEquatable<SpanId>, IComparable<SpanId>, ISerializable
    {
        private readonly byte[] _value;
        private readonly int _hashCode;

        public SpanId(byte[] value)
        {
            _value = value;
            _hashCode = GetHashCode(value);
        }

        public SpanId(byte[] value, int hashCode)
        {
            _value = value;
            _hashCode = hashCode;
        }

        public SpanId(SerializationInfo info, StreamingContext context)
        {
            _value = (byte[])info.GetValue("v", typeof(byte[]));
            _hashCode = info.GetInt32("h");
        }

        public static SpanId Create(string id) => new SpanId(Encoding.UTF8.GetBytes(id));

        public readonly ReadOnlyMemory<byte> Value => _value;

        public readonly bool IsDefault => _value is null || _value.Length == 0;

        public override readonly bool Equals(object obj)
        {
            return obj is SpanId kind && this.Equals(kind);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(SpanId obj)
        {
            if (object.ReferenceEquals(_value, obj._value)) return true;
            if (_value is null ^ obj._value is null) return false;
            return _value.AsSpan().SequenceEqual(obj._value);
        }

        public static int GetHashCode(byte[] value) => (int)JenkinsHash.ComputeHash(value);

        public override readonly int GetHashCode() => _hashCode;

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", _value);
            info.AddValue("h", _hashCode);
        }

        public static byte[] UnsafeGetArray(SpanId id) => id._value;

        public int CompareTo(SpanId other) => _value.AsSpan().SequenceCompareTo(other._value.AsSpan());

        public override string ToString() => this.ToStringUtf8();

        public readonly string ToStringUtf8()
        {
            if (_value is object) return Encoding.UTF8.GetString(_value);
            return null;
        }

        public sealed class Comparer : IEqualityComparer<SpanId>, IComparer<SpanId>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(SpanId x, SpanId y) => x.CompareTo(y);

            public bool Equals(SpanId x, SpanId y) => x.Equals(y);

            public int GetHashCode(SpanId obj) => obj.GetHashCode();
        }
    }
}
