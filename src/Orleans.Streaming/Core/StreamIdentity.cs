
using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream identity contains the public stream information use to uniquely identify a stream.
    /// Stream identities are only unique per stream provider.
    /// </summary>
    /// <remarks>
    /// Use <see cref="StreamId"/> where possible, instead.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public class StreamIdentity : IStreamIdentity
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamIdentity"/> class.
        /// </summary>
        /// <param name="streamGuid">The stream unique identifier.</param>
        /// <param name="streamNamespace">The stream namespace.</param>
        public StreamIdentity(Guid streamGuid, string streamNamespace)
        {
            Guid = streamGuid;
            Namespace = streamNamespace;
        }

        /// <summary>
        /// Gets the stream identifier.
        /// </summary>
        [Id(1)]
        public Guid Guid { get; }

        /// <summary>
        /// Gets the stream namespace.
        /// </summary>
        [Id(2)]
        public string Namespace { get; }

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is StreamIdentity identity && this.Guid.Equals(identity.Guid) && this.Namespace == identity.Namespace;

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hashCode = -1455462324;
            hashCode = hashCode * -1521134295 + this.Guid.GetHashCode();
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(this.Namespace);
            return hashCode;
        }
    }
}
