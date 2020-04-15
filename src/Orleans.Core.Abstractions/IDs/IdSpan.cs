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
    public readonly struct IdSpan : IEquatable<IdSpan>, IComparable<IdSpan>, ISerializable
    {
        private readonly byte[] _value;
        private readonly int _hashCode;

        public IdSpan(byte[] value)
        {
            _value = value;
            _hashCode = GetHashCode(value);
        }

        private IdSpan(byte[] value, int hashCode)
        {
            _value = value;
            _hashCode = hashCode;
        }

        public IdSpan(SerializationInfo info, StreamingContext context)
        {
            _value = (byte[])info.GetValue("v", typeof(byte[]));
            _hashCode = info.GetInt32("h");
        }

        public ReadOnlyMemory<byte> Value => _value;

        public bool IsDefault => _value is null || _value.Length == 0;

        public static IdSpan Create(string id) => new IdSpan(Encoding.UTF8.GetBytes(id));

        public ReadOnlySpan<byte> AsSpan() => _value;

        public override bool Equals(object obj)
        {
            return obj is IdSpan kind && this.Equals(kind);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(IdSpan obj)
        {
            if (object.ReferenceEquals(_value, obj._value)) return true;
            if (_value is null || obj._value is null) return false;
            return _value.AsSpan().SequenceEqual(obj._value);
        }

        public static int GetHashCode(byte[] value) => (int)JenkinsHash.ComputeHash(value);

        public override int GetHashCode() => _hashCode;

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", _value);
            info.AddValue("h", _hashCode);
        }

        public static IdSpan UnsafeCreate(byte[] value, int hashCode) => new IdSpan(value, hashCode);

        public static byte[] UnsafeGetArray(IdSpan id) => id._value;

        public int CompareTo(IdSpan other) => _value.AsSpan().SequenceCompareTo(other._value.AsSpan());

        public override string ToString() => this.ToStringUtf8();

        public string ToStringUtf8()
        {
            if (_value is object) return Encoding.UTF8.GetString(_value);
            return null;
        }

        public static bool operator ==(IdSpan a, IdSpan b) => a.Equals(b);

        public static bool operator !=(IdSpan a, IdSpan b) => !a.Equals(b);

        public static bool operator >(IdSpan a, IdSpan b) => a.CompareTo(b) > 0;

        public static bool operator <(IdSpan a, IdSpan b) => a.CompareTo(b) < 0;

        public sealed class Comparer : IEqualityComparer<IdSpan>, IComparer<IdSpan>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(IdSpan x, IdSpan y) => x.CompareTo(y);

            public bool Equals(IdSpan x, IdSpan y) => x.Equals(y);

            public int GetHashCode(IdSpan obj) => obj.GetHashCode();
        }
    }
}
