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
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream queue storage adapter.  This is an abstraction layer that hides the implementation details of the underlying queuing system.
    /// </summary>
    public interface IQueueAdapter
    {
        /// <summary>
        /// Name of the adapter. Primarily for logging purposes
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Writes a set of events to the queue as a single batch associated with the provided streamId.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="streamId"></param>
        /// <param name="events"></param>
        /// <returns></returns>
        Task QueueMessageBatchAsync<T>(Guid streamGuid, String streamNamespace, IEnumerable<T> events);

        /// <summary>
        /// Creates a quere receiver for the specificed queueId
        /// </summary>
        /// <param name="requesterId">Id of requester, for monitoring/logging purposes</param>
        /// <param name="queueId"></param>
        /// <returns></returns>
        IQueueAdapterReceiver CreateReceiver(QueueId queueId);

        /// <summary>
        /// Determines whether this is a rewindable stream adapter - supports subscribing from previous point in time.
        /// </summary>
        /// <returns>True if this is a rewindable stream adapter, false otherwise.</returns>
        bool IsRewindable { get; }

        /// <summary>
        /// Direction of this queue adapter: Read, Write or ReadWrite.
        /// </summary>
        /// <returns>The direction in which this adapter provides data.</returns>
        StreamProviderDirection Direction { get; }
    }

    public static class QueueAdapterExtensions
    {
        /// <summary>
        /// Writes a set of events to the queue as a single batch associated with the provided streamId.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="adapter"></param>
        /// <param name="streamId"></param>
        /// <param name="evt"></param>
        /// <returns></returns>
        public static Task QueueMessageAsync<T>(this IQueueAdapter adapter, Guid streamGuid, String streamNamespace, T evt)
        {
            return adapter.QueueMessageBatchAsync(streamGuid, streamNamespace, new[] { evt });
        }
    }
}
