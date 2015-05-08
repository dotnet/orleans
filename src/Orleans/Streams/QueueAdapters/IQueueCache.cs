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

namespace Orleans.Streams
{
    public interface IQueueCache
    {
        /// <summary>
        /// The id of the queue for which this cache is caching data.
        /// </summary>
        QueueId Id { get; }

        /// <summary>
        /// Current cache size.
        /// </summary>
        int Size { get; }

        /// <summary>
        /// The limit of the maximum number of items that can be added to the cache in a single AddToCache operation.
        /// </summary>
        int MaxAddCount { get; }

        /// <summary>
        /// Add messages to the cache
        /// </summary>
        /// <param name="messages"></param>
        void AddToCache(IList<IBatchContainer> messages);

        /// <summary>
        /// Acquire a stream message cursor.  This can be used to retreave messages from the
        ///   cache starting at the location indicated by the provided token.
        /// </summary>
        /// <param name="streamGuid"></param>
        /// <param name="streamNamespace"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        IQueueCacheCursor GetCacheCursor(Guid streamGuid, string streamNamespace, StreamSequenceToken token);

        /// <summary>
        /// Returns true if this cache is under pressure.
        /// </summary>
        bool IsUnderPressure();
    }
}
