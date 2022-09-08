
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    /// <summary>
    /// <see cref="IBatchContainer"/> implementation for generated event payloads.
    /// </summary>
    [GenerateSerializer]
    public sealed class GeneratedBatchContainer : IBatchContainer
    {
        /// <inheritdoc />
        [Id(0)]
        public StreamId StreamId { get; }

        /// <inheritdoc />
        public StreamSequenceToken SequenceToken => RealToken;

        /// <summary>
        /// Gets the real token.
        /// </summary>
        /// <value>The real token.</value>
        [Id(1)]
        public EventSequenceTokenV2 RealToken { get;  }

        /// <summary>
        /// Gets the enqueue time (UTC).
        /// </summary>
        /// <value>The enqueue time (UTC).</value>
        [Id(2)]
        public DateTime EnqueueTimeUtc { get; }

        /// <summary>
        /// Gets the payload.
        /// </summary>
        /// <value>The payload.</value>
        [Id(3)]
        public object Payload { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="GeneratedBatchContainer"/> class.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="payload">The payload.</param>
        /// <param name="token">The token.</param>
        public GeneratedBatchContainer(StreamId streamId, object payload, EventSequenceTokenV2 token)
        {
            StreamId = streamId;
            EnqueueTimeUtc = DateTime.UtcNow;
            this.Payload = payload;
            this.RealToken = token;
        }

        /// <inheritdoc />
        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return new[] { Tuple.Create((T)Payload, SequenceToken) };
        }

        /// <inheritdoc />
        public bool ImportRequestContext()
        {
            return false;
        }
    }
}
