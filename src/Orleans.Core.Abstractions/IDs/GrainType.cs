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
        public GrainType(IdSpan id) => Value = id;
        public GrainType(byte[] value) => Value = new IdSpan(value);

        public GrainType(SerializationInfo info, StreamingContext context)
        {
            Value = IdSpan.UnsafeCreate((byte[])info.GetValue("v", typeof(byte[])), info.GetInt32("h"));
        }

        public IdSpan Value { get; }

        public ReadOnlySpan<byte> AsSpan() => this.Value.AsSpan();

        public static GrainType Create(string value) => new GrainType(Encoding.UTF8.GetBytes(value));

        public static explicit operator IdSpan(GrainType kind) => kind.Value;

        public static explicit operator GrainType(IdSpan id) => new GrainType(id);

        public bool IsDefault => Value.IsDefault;

        public override bool Equals(object obj) => obj is GrainType kind && this.Equals(kind);

        public bool Equals(GrainType obj) => Value.Equals(obj.Value);

        public override int GetHashCode() => Value.GetHashCode();

        public static byte[] UnsafeGetArray(GrainType id) => IdSpan.UnsafeGetArray(id.Value);


        public int CompareTo(GrainType other) => Value.CompareTo(other.Value);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("v", IdSpan.UnsafeGetArray(Value));
            info.AddValue("h", Value.GetHashCode());
        }

        public override string ToString() => this.ToStringUtf8();

        public string ToStringUtf8() => Value.ToStringUtf8();

        public sealed class Comparer : IEqualityComparer<GrainType>, IComparer<GrainType>
        {
            public static Comparer Instance { get; } = new Comparer();

            public int Compare(GrainType x, GrainType y) => x.CompareTo(y);

            public bool Equals(GrainType x, GrainType y) => x.Equals(y);

            public int GetHashCode(GrainType obj) => obj.GetHashCode();
        }
    }
}
