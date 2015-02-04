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
using System.Threading.Tasks;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    public class SimpleQueueAdapterCacheCursor : IQueueAdapterCacheCursor
    {
        private readonly Guid streamGuid;
        private readonly string streamNamespace;
        private readonly object cursor;
        private readonly IQueueAdapterCache cache;
        private IBatchContainer current;

        public SimpleQueueAdapterCacheCursor(IQueueAdapterCache cache, Guid streamGuid, string streamNamespace, EventSequenceToken sequenceToken)
        {
            if (cache == null)
            {
                throw new ArgumentNullException("cache");
            }
            this.cache = cache;
            this.streamGuid = streamGuid;
            this.streamNamespace = streamNamespace;
            cursor = this.cache.GetCursor(sequenceToken);
        }

        public IBatchContainer GetCurrent(out Exception exception)
        {
            exception = null;
            return current;
        }

        public bool MoveNext()
        {
            IBatchContainer next;
            double backPressure;
            while (cache.TryGetNextMessage(cursor, out next, out backPressure) && !IsInStream(next))
            {
            }

            if (!IsInStream(next))
                return false;
                
            current = next;
            return true;
        }

        private bool IsInStream(IBatchContainer batchContainer)
        {
            return batchContainer != null &&
                    batchContainer.StreamGuid == streamGuid &&
                    batchContainer.StreamNamespace == streamNamespace;
        }
    }
}