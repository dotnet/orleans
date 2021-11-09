using System;
using Azure;
using Azure.Data.Tables;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.PersistentStreams
{
    /// <summary>
    /// Delivery failure table storage entity.
    /// </summary>
    public class StreamDeliveryFailureEntity : ITableEntity
    {
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        /// <summary>
        /// Id of the subscription on which this delivery failure occurred.
        /// </summary>
        public Guid SubscriptionId { get; set; }

        /// <summary>
        /// Name of the stream provider generating this failure.
        /// </summary>
        public string StreamProviderName { get; set; }

        /// <summary>
        /// Guid Id of the stream on which the failure occurred.
        /// </summary>
        public string StreamGuid { get; set; }

        /// <summary>
        /// Namespace of the stream on which the failure occurred.
        /// </summary>
        public string StreamNamespace { get; set; }

        /// <summary>
        /// Serialized sequence token of the event that failed delivery.
        /// </summary>
        public byte[] SequenceToken { get; set; }

        /// <summary>
        /// Sets the partition key before persist call.
        /// </summary>
        public virtual void SetPartitionKey(string deploymentId)
        {
            PartitionKey = MakeDefaultPartitionKey(StreamProviderName, deploymentId);
        }

        /// <summary>
        /// Default partition key
        /// </summary>
        public static string MakeDefaultPartitionKey(string streamProviderName, string deploymentId)
        {
            return $"DeliveryFailure_{streamProviderName}_{deploymentId}";
        }

        /// <summary>
        /// Sets the row key before persist call
        /// </summary>
        public virtual void SetRowkey()
        {
            RowKey = $"{ReverseOrderTimestampTicks():x16}_{Guid.NewGuid()}";
        }

        /// <summary>
        /// Sets sequence token by serializing it to property.
        /// </summary>
        /// <param name="serializer"></param>
        /// <param name="token"></param>
        public virtual void SetSequenceToken(Serializer<StreamSequenceToken> serializer, StreamSequenceToken token)
        {
            SequenceToken = token != null ? serializer.SerializeToArray(token) : null;
        }

        /// <summary>
        /// Gets sequence token by deserializing it from property.
        /// </summary>
        /// <returns></returns>
        public virtual StreamSequenceToken GetSequenceToken(Serializer<StreamSequenceToken> serializer)
        {
            return SequenceToken != null ? serializer.Deserialize(SequenceToken) : null;
        }

        /// <summary>
        /// Returns the number of ticks from now (UTC) to the year 9683.
        /// </summary>
        /// <remarks>
        /// This is useful for ordering the most recent failures at the start of the partition.  While useful
        ///  for efficient table storage queries, under heavy failure load this may cause a hot spot in the
        ///  table. This is not an expected occurrence, but if it happens, we recommend subdividing your row
        ///  key with some other field (stream namespace?).
        /// </remarks>
        /// <returns></returns>
        protected static long ReverseOrderTimestampTicks()
        {
            var now = DateTime.UtcNow;
            return DateTime.MaxValue.Ticks - now.Ticks;
        }
    }
}
