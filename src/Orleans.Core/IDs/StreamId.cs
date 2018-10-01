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
            () => new Interner<(Guid Guid, string ProviderName, string Namespace), StreamId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq));

        private readonly (Guid Guid, string ProviderName, string Namespace) Key;

        [NonSerialized]
        private readonly Lazy<uint> UniformHashCode;

        public Guid Guid => Key.Guid;
        public string Namespace => Key.Namespace;
        public string ProviderName => Key.ProviderName;

        private StreamId((Guid Guid, string ProviderName, string Namespace) key)
        {
            Key = key;
            UniformHashCode = new Lazy<uint>(CalculateUniformHashCode);
        }

        internal static StreamId GetStreamId(Guid guid, string providerName, string streamNamespace)
            => FindOrCreateStreamId((guid, providerName, streamNamespace));

        private static StreamId FindOrCreateStreamId((Guid Guid, string ProviderName, string Namespace) key)
            => streamIdInternCache.Value.FindOrCreate(key, k => new StreamId(k));

        public bool Equals(StreamId other)
            => Key.Equals(other?.Key);

        public override bool Equals(object other)
            => Equals(other as StreamId);

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

        /// <remarks>
        /// We could consider replacing the body of this method with <code>=> Key.GetHashCode()</code>.
        /// Doing so would result in returning a different value of the hash code than was returned by the original 
        /// implementation, so for now, pending feedback, I decided to use an implementation that returned
        /// exactly the same value as previous implementations, instead of opting for a simpler (and better) implementation,
        /// keeping in mind that someone somewhere might be depending on an unchanged hash code implementation.
        /// </remarks>
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
        
        uint CalculateUniformHashCode()
        {
            var guidBytes = Guid.ToByteArray();
            var providerBytes = GetBytes(ProviderName);
            var namespaceBytes = GetBytes(Namespace);
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
