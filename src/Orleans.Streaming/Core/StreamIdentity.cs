
using System;
using System.Collections.Generic;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream identity contains the public stream information use to uniquely identify a stream.
    /// Stream identities are only unique per stream provider.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class StreamIdentity : IStreamIdentity
    {
        public StreamIdentity(Guid streamGuid, string streamNamespace)
        {
            Guid = streamGuid;
            Namespace = streamNamespace;
        }

        /// <summary>
        /// Stream primary key guid.
        /// </summary>
        [Id(1)]
        public Guid Guid { get; }

        /// <summary>
        /// Stream namespace.
        /// </summary>
        [Id(2)]
        public string Namespace { get; }

        public override bool Equals(object obj) => obj is StreamIdentity identity && this.Guid.Equals(identity.Guid) && this.Namespace == identity.Namespace;

        public override int GetHashCode()
        {
            var hashCode = -1455462324;
            hashCode = hashCode * -1521134295 + this.Guid.GetHashCode();
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(this.Namespace);
            return hashCode;
        }
    }
}
