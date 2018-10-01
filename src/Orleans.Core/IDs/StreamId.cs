using System;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{

    // TODO: Use this record type instead of the value tuple when c# language releases the feature.
    //struct StreamIdKey(Guid Guid, string ProviderName, string Namespace);

    /// <summary>
    /// Identifier of an Orleans virtual stream.
    /// </summary>
    [Serializable, Immutable]
    internal class StreamId : IStreamIdentity, IRingIdentifier<StreamId>, IEquatable<StreamId>, IComparable<StreamId>
    {

        // TODO: Integrate with Orleans serializer to get interning working even better.

        [NonSerialized]
        private static readonly Lazy<Interner<(Guid Guid, string ProviderName, string Namespace), StreamId>> streamIdInternCache = new Lazy<Interner<(Guid Guid, string ProviderName, string Namespace), StreamId>>(
            () => new Interner<(Guid Guid, string ProviderName, string Namespace), StreamId>());

        private readonly (Guid Guid, string ProviderName, string Namespace) Key;

        [NonSerialized]
        private readonly Lazy<uint> UniformHashCode;

        public Guid Guid => Key.Guid;
        public string Namespace => Key.Namespace;
        public string ProviderName => Key.ProviderName;

        private StreamId((Guid Guid, string ProviderName, string Namespace) key)
        {
            Key = key;
            UniformHashCode = new Lazy<uint>(() => CalculateUniformHashCode(key));
        }

        internal static StreamId GetStreamId(Guid guid, string providerName, string streamNamespace)
        {
            return FindOrCreateStreamId((guid, providerName, streamNamespace));
        }

        private static StreamId FindOrCreateStreamId((Guid Guid, string ProviderName, string Namespace) key)
        {
            return streamIdInternCache.Value.FindOrCreate(key, k => new StreamId(k));
        }

        public bool Equals(StreamId other)
            => Key.Equals(other.Key);

        public uint GetUniformHashCode()
            => UniformHashCode.Value;

        public int CompareTo(StreamId other)
        {
            if (null == other) return 1;
            var result = Guid.CompareTo(other.Guid);
            if (result != 0) return result;
            result = string.Compare(ProviderName, other.ProviderName, StringComparison.Ordinal);
            if (result != 0) return result;
            return string.Compare(Namespace, other.Namespace, StringComparison.Ordinal);
        }

        public override int GetHashCode()
            => Guid.GetHashCode()
                ^ (ProviderName?.GetHashCode() ?? 0)
                ^ (Namespace?.GetHashCode() ?? 0);

        public override string ToString()
        {
            var result = $"{Guid}-{ProviderName}";
            if (null != Namespace)
            {
                result = $"{Namespace}-" + result;
            }
            return result;
        }


        static uint CalculateUniformHashCode((Guid Guid, string ProviderName, string Namespace) key)
        {
            var guidBytes = key.Guid.ToByteArray();
            var providerBytes = GetBytes(key.ProviderName);
            var namespaceBytes = GetBytes(key.Namespace);
            var allBytes = new byte[guidBytes.Length + providerBytes.Length + namespaceBytes.Length];
            Buffer.BlockCopy(guidBytes, 0, allBytes, 0, guidBytes.Length);
            Buffer.BlockCopy(providerBytes, 0, allBytes, guidBytes.Length, providerBytes.Length);
            Buffer.BlockCopy(namespaceBytes, 0, allBytes, guidBytes.Length + providerBytes.Length, namespaceBytes.Length);
            return JenkinsHash.ComputeHash(allBytes);

            byte[] GetBytes(string value)
            {
                if (null == value) return new byte[0];
                return Encoding.UTF8.GetBytes(value);
            }
        }
    }
}
