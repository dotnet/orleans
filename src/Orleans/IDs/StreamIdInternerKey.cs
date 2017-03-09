using System;
using Orleans.Concurrency;

namespace Orleans.Streams
{
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