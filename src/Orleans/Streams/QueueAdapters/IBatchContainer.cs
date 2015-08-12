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
    /// <summary>
    /// Each queue message is allowed to be a heterogeneous  ordered set of events.  IBatchContainer contains these events and allows users to query the batch for a specific type of event.
    /// </summary>
    public interface IBatchContainer
    {
        /// <summary>
        /// Stream identifier for the stream this batch is part of.
        /// </summary>
        Guid StreamGuid { get; }

        /// <summary>
        /// Stream namespace for the stream this batch is part of.
        /// </summary>
        String StreamNamespace { get; }

        /// <summary>
        /// Gets events of a specific type from the batch.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        IEnumerable<Tuple<T,StreamSequenceToken>> GetEvents<T>();

        /// <summary>
        /// Stream Sequence Token for the start of this batch.
        /// </summary>
        StreamSequenceToken SequenceToken { get; }

        /// <summary>
        /// Decide whether this batch should be sent to the specified target.
        /// </summary>
        bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc);
    }
}
