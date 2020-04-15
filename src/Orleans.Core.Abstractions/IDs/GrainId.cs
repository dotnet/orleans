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
    public readonly struct GrainId : IEquatable<GrainId>, IComparable<GrainId>, ISerializable
    {
        public GrainId(GrainType type, SpanId key)
        {
            Type = type;
            Key = key;
        }

        public GrainId(byte[] type, byte[] key) : this(new GrainType(type), new SpanId(key))
        {
        }

        public GrainId(GrainType type, byte[] key) : this(type, new SpanId(key))
        {
        }

        public GrainId(SerializationInfo info, StreamingContext context)
        {
            Type = new GrainType(SpanId.UnsafeCreate((byte[])info.GetValue("tv", typeof(byte[])), info.GetInt32("th")));
            Key = SpanId.UnsafeCreate((byte[])info.GetValue("kv", typeof(byte[])), info.GetInt32("kh"));
        }

        public GrainType Type { get; }

        public SpanId Key { get; }

        // TODO: remove implicit conversion (potentially make explicit to start with)
        public static implicit operator LegacyGrainId(GrainId id) => LegacyGrainId.FromGrainId(id);

        public static GrainId Create(string type, string key) => Create(GrainType.Create(type), key);

        public static GrainId Create(string type, Guid key) => Create(GrainType.Create(type), key.ToString("N"));

        public static GrainId Create(GrainType type, string key) => new GrainId(type, Encoding.UTF8.GetBytes(key));

        public static GrainId Create(GrainType type, SpanId key) => new GrainId(type, key);

        public bool IsDefault => Type.IsDefault && Key.IsDefault;

        public override bool Equals(object obj) => obj is GrainId id && this.Equals(id);

        public bool Equals(GrainId other) => this.Type.Equals(other.Type) && this.Key.Equals(other.Key);

        public override int GetHashCode() => HashCode.Combine(Type, Key);

        public uint GetUniformHashCode() => unchecked((uint)this.GetHashCode());

        public uint GetHashCode_Modulo(uint umod)
        {
            int key = GetHashCode();
            int mod = (int)umod;
            key = ((key % mod) + mod) % mod; // key should be positive now. So assert with checked.
            return checked((uint)key);
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("tv", GrainType.UnsafeGetArray(Type));
            info.AddValue("th", Type.GetHashCode());
            info.AddValue("kv", SpanId.UnsafeGetArray(Key));
            info.AddValue("kh", Key.GetHashCode());
        }

        public int CompareTo(GrainId other)
        {
            var typeComparison = Type.CompareTo(other.Type);
            if (typeComparison != 0) return typeComparison;

            return Key.CompareTo(other.Key);
        }

        public static bool operator ==(GrainId a, GrainId b) => a.Equals(b);

        public static bool operator !=(GrainId a, GrainId b) => !a.Equals(b);

        public static bool operator >(GrainId a, GrainId b) => a.CompareTo(b) > 0;

        public static bool operator <(GrainId a, GrainId b) => a.CompareTo(b) < 0;

        public override string ToString() => $"{Type.ToStringUtf8()}/{Key.ToStringUtf8()}";

        public static (byte[] Key, int KeyHashCode) UnsafeGetKey(GrainId id) => (SpanId.UnsafeGetArray(id.Key), id.Key.GetHashCode());

        public static SpanId KeyAsSpanId(GrainId id) => id.Key;

        public sealed class Comparer : IEqualityComparer<GrainId>, IComparer<GrainId>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(GrainId x, GrainId y) => x.CompareTo(y);

            public bool Equals(GrainId x, GrainId y) => x.Equals(y);

            public int GetHashCode(GrainId obj) => obj.GetHashCode();
        }
    }
}
