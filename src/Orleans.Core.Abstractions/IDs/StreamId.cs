using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;
using Orleans.Streams;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identify a Stream within a provider
    /// </summary>
    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct StreamId : IEquatable<StreamId>, IComparable<StreamId>, ISerializable
    {
        private readonly byte[] fullKey;
        private readonly ushort keyIndex;
        private readonly int hash;

        public ReadOnlyMemory<byte> FullKey => fullKey;

        public ReadOnlyMemory<byte> Namespace => fullKey.AsMemory(0, this.keyIndex);

        public ReadOnlyMemory<byte> Key => fullKey.AsMemory(this.keyIndex);

        private StreamId(byte[] fullKey, ushort keyIndex, int hash)
        {
            this.fullKey = fullKey;
            this.keyIndex = keyIndex;
            this.hash = hash;
        }

        internal StreamId(byte[] fullKey, ushort keyIndex)
            : this(fullKey, keyIndex, (int) JenkinsHash.ComputeHash(fullKey))
        {
        }

        private StreamId(SerializationInfo info, StreamingContext context)
        {
            fullKey = (byte[]) info.GetValue("fk", typeof(byte[]));
            this.keyIndex = info.GetUInt16("ki");
            this.hash = info.GetInt32("fh");
        }


        public static StreamId Create(byte[] ns, byte[] key)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            if (ns != null)
            {
                var fullKeysBytes = new byte[ns.Length + key.Length];
                ns.CopyTo(fullKeysBytes.AsSpan());
                key.CopyTo(fullKeysBytes.AsSpan(ns.Length));
                return new StreamId(fullKeysBytes, (ushort) ns.Length);
            }
            else
            {
                return new StreamId((byte[])key.Clone(), 0);
            }
        }

        public static StreamId Create(string ns, Guid key)
        {
            if (ns is null)
            {
                var buf = new byte[32];
                Utf8Formatter.TryFormat(key, buf, out var len, 'N');
                Debug.Assert(len == 32);
                return new StreamId(buf, 0);
            }
            else
            {
                var nsLen = Encoding.UTF8.GetByteCount(ns);
                var buf = new byte[nsLen + 32];
                Encoding.UTF8.GetBytes(ns, 0, ns.Length, buf, 0);
                Utf8Formatter.TryFormat(key, buf.AsSpan(nsLen), out var len, 'N');
                Debug.Assert(len == 32);
                return new StreamId(buf, (ushort)nsLen);
            }
        }

        public static StreamId Create(string ns, string key)
        {
            if (ns is null)
                return new StreamId(Encoding.UTF8.GetBytes(key), 0);

            var nsLen = Encoding.UTF8.GetByteCount(ns);
            var keyLen = Encoding.UTF8.GetByteCount(key);
            var buf = new byte[nsLen + keyLen];
            Encoding.UTF8.GetBytes(ns, 0, ns.Length, buf, 0);
            Encoding.UTF8.GetBytes(key, 0, key.Length, buf, nsLen);
            return new StreamId(buf, (ushort)nsLen);
        }

        public static StreamId Create(IStreamIdentity streamIdentity) => Create(streamIdentity.Namespace, streamIdentity.Guid);

        public int CompareTo(StreamId other) => fullKey.AsSpan().SequenceCompareTo(other.fullKey);

        public bool Equals(StreamId other) => fullKey.AsSpan().SequenceEqual(other.fullKey);

        public override bool Equals(object obj) => obj is StreamId other ? this.Equals(other) : false;

        public static bool operator ==(StreamId s1, StreamId s2) => s1.Equals(s2);

        public static bool operator !=(StreamId s1, StreamId s2) => !s2.Equals(s1);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("fk", fullKey);
            info.AddValue("ki", this.keyIndex);
            info.AddValue("fh", this.hash);
        }

        public override string ToString()
        {
            var key = this.GetKeyAsString();
            return keyIndex == 0 ? "null/" + key : this.GetNamespace() + "/" + key;
        }

        public override int GetHashCode() => this.hash;

        public string GetKeyAsString() => Encoding.UTF8.GetString(fullKey, keyIndex, fullKey.Length - keyIndex);

        public string GetNamespace() => keyIndex == 0 ? null : Encoding.UTF8.GetString(fullKey, 0, keyIndex);
    }

    [Immutable]
    [Serializable]
    [StructLayout(LayoutKind.Auto)]
    internal readonly struct InternalStreamId : IEquatable<InternalStreamId>, IComparable<InternalStreamId>, ISerializable
    {
        public string ProviderName { get; }

        public StreamId StreamId { get; }

        public InternalStreamId(string providerName, StreamId streamId)
        {
            ProviderName = providerName;
            StreamId = streamId;
        }

        private InternalStreamId(SerializationInfo info, StreamingContext context)
        {
            ProviderName = info.GetString("pvn");
            StreamId = (StreamId) info.GetValue("sid", typeof(StreamId));
        }

        public static implicit operator StreamId(InternalStreamId internalStreamId) => internalStreamId.StreamId;

        public bool Equals(InternalStreamId other) => StreamId.Equals(other) && ProviderName.Equals(other.ProviderName);

        public override bool Equals(object obj) => obj is InternalStreamId other ? this.Equals(other) : false;

        public static bool operator ==(InternalStreamId s1, InternalStreamId s2) => s1.Equals(s2);

        public static bool operator !=(InternalStreamId s1, InternalStreamId s2) => !s2.Equals(s1);

        public int CompareTo(InternalStreamId other) => StreamId.CompareTo(other.StreamId);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("pvn", ProviderName);
            info.AddValue("sid", StreamId, typeof(StreamId));
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ProviderName.GetHashCode() * 43 ^ StreamId.GetHashCode();
            }
        }

        public override string ToString()
        {
            return $"{ProviderName}/{StreamId.ToString()}";
        }

        internal string GetNamespace() => StreamId.GetNamespace();
    }
}
