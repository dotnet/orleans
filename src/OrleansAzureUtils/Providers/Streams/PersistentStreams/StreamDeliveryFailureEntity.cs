/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.PersistentStreams
{
    /// <summary>
    /// Delivery failure table storage entity.
    /// </summary>
    public class StreamDeliveryFailureEntity : TableEntity
    {
        /// <summary>
        /// Id of the subscription on which this delivery failure occured.
        /// </summary>
        public Guid SubscriptionId { get; set; }

        /// <summary>
        /// Name of the stream provider generating this failure.
        /// </summary>
        public string StreamProviderName { get; set; }

        /// <summary>
        /// Guid Id of the stream on which the failure occured.
        /// </summary>
        public Guid StreamGuid { get; set; }

        /// <summary>
        /// Namespace of the stream on which the failure occured.
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
            PartitionKey = String.Format("DeliveryFailure_{0}_{1}", StreamProviderName, deploymentId);
        }

        /// <summary>
        /// Sets the row key before persist call
        /// </summary>
        public virtual void SetRowkey()
        {
            RowKey = String.Format("{0:x16}_{1}", ReverseOrderTimestampTicks(), Guid.NewGuid());
        }

        /// <summary>
        /// Sets sequence token by serializing it to property.
        /// </summary>
        /// <param name="token"></param>
        public virtual void SetSequenceToken(StreamSequenceToken token)
        {
            SequenceToken = token != null ? GetTokenBytes(token) : null;
        }

        /// <summary>
        /// Gets sequence token by deserializing it from property.
        /// </summary>
        /// <returns></returns>
        public virtual StreamSequenceToken GetSequenceToken()
        {
            return SequenceToken != null ? TokenFromBytes(SequenceToken) : default(StreamSequenceToken);
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
        static protected long ReverseOrderTimestampTicks()
        {
            var now = DateTime.UtcNow;
            return DateTime.MaxValue.Ticks - now.Ticks;
        }

        static private byte[] GetTokenBytes(StreamSequenceToken token)
        {
            var bodyStream = new BinaryTokenStreamWriter();
            SerializationManager.Serialize(token, bodyStream);
            return bodyStream.ToByteArray();
        }

        static private StreamSequenceToken TokenFromBytes(byte[] bytes)
        {
            var stream = new BinaryTokenStreamReader(bytes);
            return SerializationManager.Deserialize<StreamSequenceToken>(stream);
        }
    }
}
