using System;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Identifier of an Orleans virtual stream.
    /// </summary>
    [Serializable]
    [Immutable]
    internal class StreamId : IStreamIdentity, IRingIdentifier<StreamId>, IEquatable<StreamId>, IComparable<StreamId>, ISerializable
    {
        [NonSerialized]
        private static readonly Lazy<Interner<StreamIdInternerKey, StreamId>> streamIdInternCache = new Lazy<Interner<StreamIdInternerKey, StreamId>>(
            () => new Interner<StreamIdInternerKey, StreamId>(InternerConstants.SIZE_LARGE, InternerConstants.DefaultCacheCleanupFreq));

        [NonSerialized]
        private uint uniformHashCache;
        private readonly StreamIdInternerKey key;

        // Keep public, similar to GrainId.GetPrimaryKey. Some app scenarios might need that.
        public Guid Guid { get { return key.Guid; } }

        // I think it might be more clear if we called this the ActivationNamespace.
        public string Namespace { get { return key.Namespace; } }

        public string ProviderName { get { return key.ProviderName; } }

        // TODO: need to integrate with Orleans serializer to really use Interner.
        private StreamId(StreamIdInternerKey key)
        {
            this.key = key;
        }

        internal static StreamId GetStreamId(Guid guid, string providerName, string streamNamespace)
        {
            return FindOrCreateStreamId(new StreamIdInternerKey(guid, providerName, streamNamespace));
        }

        private static StreamId FindOrCreateStreamId(StreamIdInternerKey key)
        {
            return streamIdInternCache.Value.FindOrCreate(key, k => new StreamId(k));
        }

        #region IComparable<StreamId> Members

        public int CompareTo(StreamId other)
        {
            return key.CompareTo(other.key);
        }

        #endregion

        #region IEquatable<StreamId> Members

        public bool Equals(StreamId other)
        {
            return other != null && key.Equals(other.key);
        }

        #endregion

        public override bool Equals(object obj)
        {
            return Equals(obj as StreamId);
        }

        public override int GetHashCode()
        {
            return key.GetHashCode();
        }

        public uint GetUniformHashCode()
        {
            if (uniformHashCache == 0)
            {
                byte[] guidBytes = Guid.ToByteArray();
                byte[] providerBytes = Encoding.UTF8.GetBytes(ProviderName);
                byte[] allBytes;
                if (Namespace == null)
                {
                    allBytes = new byte[guidBytes.Length + providerBytes.Length];
                    Array.Copy(guidBytes, allBytes, guidBytes.Length);
                    Array.Copy(providerBytes, 0, allBytes, guidBytes.Length, providerBytes.Length);
                }
                else
                {
                    byte[] namespaceBytes = Encoding.UTF8.GetBytes(Namespace);
                    allBytes = new byte[guidBytes.Length + providerBytes.Length + namespaceBytes.Length];
                    Array.Copy(guidBytes, allBytes, guidBytes.Length);
                    Array.Copy(providerBytes, 0, allBytes, guidBytes.Length, providerBytes.Length);
                    Array.Copy(namespaceBytes, 0, allBytes, guidBytes.Length + providerBytes.Length, namespaceBytes.Length);
                }
                uniformHashCache = JenkinsHash.ComputeHash(allBytes);
            }
            return uniformHashCache;
        }

        public override string ToString()
        {
            return Namespace == null ? 
                Guid.ToString() : 
                String.Format("{0}{1}-{2}", Namespace != null ? (String.Format("{0}-", Namespace)) : "", Guid, ProviderName);
        }

        #region ISerializable Members

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Use the AddValue method to specify serialized values.
            info.AddValue("Guid", Guid, typeof(Guid));
            info.AddValue("ProviderName", ProviderName, typeof(string));
            info.AddValue("Namespace", Namespace, typeof(string));
        }

        // The special constructor is used to deserialize values. 
        protected StreamId(SerializationInfo info, StreamingContext context)
        {
            // Reset the property value using the GetValue method.
            var guid = (Guid) info.GetValue("Guid", typeof(Guid));
            var providerName = (string) info.GetValue("ProviderName", typeof(string));
            var nameSpace = (string) info.GetValue("Namespace", typeof(string));
            key = new StreamIdInternerKey(guid, providerName, nameSpace);
        }
        #endregion
    }

    [Serializable]
    [Immutable]
    internal struct StreamIdInternerKey : IComparable<StreamIdInternerKey>, IEquatable<StreamIdInternerKey>
    {
        internal readonly Guid Guid;
        internal readonly string ProviderName;
        internal readonly string Namespace;

        public StreamIdInternerKey(Guid guid, string providerName, string streamNamespace)
        {
            if (string.IsNullOrWhiteSpace(providerName))
            {
                throw new ArgumentException("Provider name is null or whitespace", "providerName");
            }

            Guid = guid;
            ProviderName = providerName;
            if (streamNamespace == null)
            {
                Namespace = null;
            }
            else
            {
                if (String.IsNullOrWhiteSpace(streamNamespace))
                {
                    throw new ArgumentException("namespace must be null or substantive (not empty or whitespace).");
                }

                Namespace = streamNamespace.Trim();
            }
        }

        public int CompareTo(StreamIdInternerKey other)
        {
            int cmp1 = Guid.CompareTo(other.Guid);
            if (cmp1 == 0)
            {
                int cmp2 = string.Compare(ProviderName, other.ProviderName, StringComparison.Ordinal);
                return cmp2 == 0 ? string.Compare(Namespace, other.Namespace, StringComparison.Ordinal) : cmp2;
            }
            
            return cmp1;
        }

        public bool Equals(StreamIdInternerKey other)
        {
            return Guid.Equals(other.Guid) && Object.Equals(ProviderName, other.ProviderName) && Object.Equals(Namespace, other.Namespace);
        }

        public override int GetHashCode()
        {
            return Guid.GetHashCode() ^ (ProviderName != null ? ProviderName.GetHashCode() : 0) ^ (Namespace != null ? Namespace.GetHashCode() : 0);
        }
    }
}
