using System;
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
        private readonly ushort keyIndex;
        private readonly int hash;

        public ReadOnlyMemory<byte> FullKey { get; }

        public ReadOnlyMemory<byte> Namespace => FullKey.Slice(0, this.keyIndex);

        public ReadOnlyMemory<byte> Key => FullKey.Slice(this.keyIndex, FullKey.Length - this.keyIndex);

        internal StreamId(Memory<byte> fullKey, ushort keyIndex, int hash)
        {
            FullKey = fullKey;
            this.keyIndex = keyIndex;
            this.hash = hash;
        }

        internal StreamId(Memory<byte> fullKey, ushort keyIndex)
            : this(fullKey, keyIndex, (int) JenkinsHash.ComputeHash(fullKey.ToArray()))
        {
        }

        private StreamId(SerializationInfo info, StreamingContext context)
        {
            FullKey = new Memory<byte>((byte[]) info.GetValue("fk", typeof(byte[])));
            this.keyIndex = (ushort) info.GetValue("ki", typeof(ushort));
            this.hash = (int) info.GetValue("fh", typeof(int));
        }


        public static StreamId Create(byte[] ns, byte[] key)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            if (ns != null)
            {
                var fullKeysBytes = new byte[ns.Length + key.Length];
                Array.Copy(ns, 0, fullKeysBytes, 0, ns.Length);
                Array.Copy(key, 0, fullKeysBytes, ns.Length, key.Length);
                return new StreamId(fullKeysBytes, (ushort) ns.Length);
            }
            else
            {
                var fullKeysBytes = new byte[key.Length];
                Array.Copy(key, 0, fullKeysBytes, 0, key.Length);
                return new StreamId(fullKeysBytes, 0);
            }
        }

        public static StreamId Create(string ns, Guid key) => Create(ns, key.ToString("N"));

        public static StreamId Create(string ns, string key) => Create(ns != null ? Encoding.UTF8.GetBytes(ns) : null, Encoding.UTF8.GetBytes(key));

        public static StreamId Create(IStreamIdentity streamIdentity) => Create(streamIdentity.Namespace, streamIdentity.Guid);

        public int CompareTo(StreamId other) => FullKey.Span.SequenceCompareTo(other.FullKey.Span);

        public bool Equals(StreamId other) => FullKey.Span.SequenceEqual(other.FullKey.Span);

        public override bool Equals(object obj) => obj is StreamId other ? this.Equals(other) : false;

        public static bool operator ==(StreamId s1, StreamId s2) => s1.Equals(s2);

        public static bool operator !=(StreamId s1, StreamId s2) => !s2.Equals(s1);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("fk", FullKey.ToArray());
            info.AddValue("ki", this.keyIndex);
            info.AddValue("fh", this.hash);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            if (!Namespace.IsEmpty)
                sb.Append(this.GetNamespace());
            else
                sb.Append("null");

            sb.Append($"/{this.GetKeyAsString()}");

            return sb.ToString();
        }

        public override int GetHashCode() => this.hash;
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
            ProviderName = (string) info.GetValue("pvn", typeof(string));
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
    }

    public static class StreamIdExtensions
    {
        public static string GetKeyAsString(this StreamId streamId)
        {
#if NETCOREAPP
            var bytes = streamId.Key;
#else
            var bytes = streamId.Key.ToArray();
#endif
            return Encoding.UTF8.GetString(bytes);
        }

        public static string GetNamespace(this StreamId streamId)
        {
            if (streamId.Namespace.IsEmpty)
                return null;
#if NETCOREAPP 
            var bytes = streamId.Namespace;
#else
            var bytes = streamId.Namespace.ToArray();
#endif
            return Encoding.UTF8.GetString(bytes);
        }

        internal static string GetKeyAsString(this InternalStreamId internalStreamId) => ((StreamId)internalStreamId).GetKeyAsString();

        internal static string GetNamespace(this InternalStreamId internalStreamId) => internalStreamId.StreamId.GetNamespace();
    }
}
