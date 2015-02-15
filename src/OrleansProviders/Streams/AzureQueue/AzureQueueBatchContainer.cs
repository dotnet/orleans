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

﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    [Serializable]
    internal class AzureQueueBatchContainer : IBatchContainer
    {
        private EventSequenceToken sequenceToken;
        private readonly List<object> events;

        [NonSerialized]
        // Need to store reference to the original AQ CloudQueueMessage to be able to delete it later on.
        // Don't need to serialize it, since we are never interested in sending it to stream consumers.
        internal CloudQueueMessage CloudQueueMessage;
        
        public Guid StreamGuid { get; private set; }
        public String StreamNamespace { get; private set; }

        public StreamSequenceToken SequenceToken 
        {
            get { return sequenceToken; }
        }

        private AzureQueueBatchContainer(Guid streamGuid, String streamNamespace, List<object> events)
        {
            if (events == null) throw new ArgumentNullException("events", "Message contains no events");
            
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            this.events = events;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return events.OfType<T>().Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, sequenceToken.CreateSequenceTokenForEvent(i)));
        }

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            foreach (object item in events)
            {
                if (shouldReceiveFunc(stream, filterData, item))
                    return true; // There is something in this batch that the consumer is intereted in, so we should send it.
            }
            return false; // Consumer is not interested in any of these events, so don't send.
        }

        internal static CloudQueueMessage ToCloudQueueMessage<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events)
        {
            var azureQueueBatchMessage = new AzureQueueBatchContainer(streamGuid, streamNamespace, events.Cast<object>().ToList());
            var rawBytes = SerializationManager.SerializeToByteArray(azureQueueBatchMessage);
            return new CloudQueueMessage(rawBytes);
        }

        internal static AzureQueueBatchContainer FromCloudQueueMessage(CloudQueueMessage cloudMsg, long sequenceId)
        {
            var azureQueueBatch = SerializationManager.DeserializeFromByteArray<AzureQueueBatchContainer>(cloudMsg.AsBytes);
            azureQueueBatch.CloudQueueMessage = cloudMsg;
            azureQueueBatch.sequenceToken = new EventSequenceToken(sequenceId);
            return azureQueueBatch;
        }

        public override string ToString()
        {
            return string.Format("AzureQueueBatchContainer:Stream={0},#Items={1}", StreamGuid, events.Count);
        }
    }
}