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

using Orleans.Providers.Streams.PersistentStreams;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// The entity records stream event delivery failure information for streams from azure queues
    /// </summary>
    public class AzureQueueDeliveryFailureEntity : StreamDeliveryFailureEntity
    {
        /// <summary>
        /// This is the default table name where these entites should be stored
        /// </summary>
        public const string DefaultTableName = "AzureQueueDeliveryFailures";

        /// <summary>
        /// Static factory function for creating this entity by queueId.
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public static AzureQueueDeliveryFailureEntity Create(QueueId queueId)
        {
            return new AzureQueueDeliveryFailureEntity()
            {
                QueueName = queueId.ToString()
            };
        }

        /// <summary>
        /// Azure queue form which failing event came from
        /// </summary>
        public string QueueName { get; set; }
    }
}
