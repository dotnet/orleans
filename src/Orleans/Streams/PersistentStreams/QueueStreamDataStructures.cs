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
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal enum StreamConsumerDataState
    {
        Active, // Indicates that events are activly being delivered to this consumer.
        Inactive, // Indicates that events are not activly being delivered to this consumers.  If adapter produces any events on this consumers stream, the agent will need begin delivering events
    }

    [Serializable]
    internal class StreamConsumerData
    {
        public GuidId SubscriptionId;
        public StreamId StreamId;
        public IStreamConsumerExtension StreamConsumer;
        public StreamConsumerDataState State = StreamConsumerDataState.Inactive;
        public IQueueCacheCursor Cursor;
        public IStreamFilterPredicateWrapper Filter;
        public StreamHandshakeToken LastToken;

        public StreamConsumerData(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            SubscriptionId = subscriptionId;
            StreamId = streamId;
            StreamConsumer = streamConsumer;
            Filter = filter;
        }

        internal void SafeDisposeCursor(Logger logger)
        {
            try
            {
                if (Cursor != null)
                {
                    // kill cursor activity and ensure it does not start again on this consumer data.
                    Utils.SafeExecute(Cursor.Dispose, logger,
                        () => String.Format("Cursor.Dispose on stream {0}, StreamConsumer {1} has thrown exception.", StreamId, StreamConsumer));
                }
            }
            finally
            {
                Cursor = null;
            }
        }
    }
}
