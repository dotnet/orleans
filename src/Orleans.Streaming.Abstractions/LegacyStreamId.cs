using System;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Identifier of an Orleans virtual stream.
    /// </summary>
    [Serializable]
    [Immutable]
    [GenerateSerializer]
    internal sealed class LegacyStreamId : IStreamIdentity, IRingIdentifier<LegacyStreamId>, IEquatable<LegacyStreamId>, IComparable<LegacyStreamId>, ISerializable
    {
        private static readonly Interner<StreamIdInternerKey, LegacyStreamId> streamIdInternCache = new Interner<StreamIdInternerKey, LegacyStreamId>(InternerConstants.SIZE_LARGE);

        [NonSerialized]
        private uint uniformHashCache;
        [Id(1)]
        private readonly StreamIdInternerKey key;

        // Keep public, similar to GrainId.GetPrimaryKey. Some app scenarios might need that.
        public Guid Guid { get { return key.Guid; } }

        // I think it might be more clear if we called this the ActivationNamespace.
        public string Namespace { get { return key.Namespace; } }

        public string ProviderName { get { return key.ProviderName; } }

        // TODO: need to integrate with Orleans serializer to really use Interner.
        private LegacyStreamId(StreamIdInternerKey key)
        {
            this.key = key;
        }

        internal static LegacyStreamId GetStreamId(Guid guid, string providerName, string streamNamespace)
        {
            var key = new StreamIdInternerKey(guid, providerName, streamNamespace);
            return streamIdInternCache.FindOrCreate(key, k => new LegacyStreamId(k));
        }

        public int CompareTo(LegacyStreamId other)
        {
            return key.CompareTo(other.key);
        }

        public bool Equals(LegacyStreamId other)
        {
            return other != null && key.Equals(other.key);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as LegacyStreamId);
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
                String.Format("{0}-{1}-{2}", Namespace, Guid, ProviderName);
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            // Use the AddValue method to specify serialized values.
            info.AddValue("Guid", Guid, typeof(Guid));
            info.AddValue("ProviderName", ProviderName, typeof(string));
            info.AddValue("Namespace", Namespace, typeof(string));
        }

        // The special constructor is used to deserialize values. 
        private LegacyStreamId(SerializationInfo info, StreamingContext context)
        {
            // Reset the property value using the GetValue method.
            var guid = (Guid)info.GetValue("Guid", typeof(Guid));
            var providerName = info.GetString("ProviderName");
            var nameSpace = info.GetString("Namespace");
            key = new StreamIdInternerKey(guid, providerName, nameSpace);
        }
    }

    [Serializable]
    [Immutable]
    [GenerateSerializer]
    internal readonly struct StreamIdInternerKey : IComparable<StreamIdInternerKey>, IEquatable<StreamIdInternerKey>
    {
        [Id(1)]
        internal readonly Guid Guid;
        [Id(2)]
        internal readonly string ProviderName;
        [Id(3)]
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
                int cmp2 = string.CompareOrdinal(ProviderName, other.ProviderName);
                return cmp2 == 0 ? string.CompareOrdinal(Namespace, other.Namespace) : cmp2;
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
