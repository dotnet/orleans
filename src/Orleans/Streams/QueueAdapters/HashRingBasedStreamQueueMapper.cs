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
using Orleans.Runtime;

namespace Orleans.Streams
{
    public class HashRingBasedStreamQueueMapper : IStreamQueueMapper
    {
        private readonly int numQueues;
        private readonly HashRing<QueueId> hashRing;

        public HashRingBasedStreamQueueMapper(int nQueues, string queueNamePrefix)
        {
            numQueues = nQueues;
            var queueIds = new List<QueueId>(numQueues);
            if (nQueues == 1)
            {
                uint uniformHashCode = 0;
                queueIds.Add(QueueId.GetQueueId(queueNamePrefix, 0, uniformHashCode));
            }
            else
            {
                uint portion = checked((uint)(RangeFactory.RING_SIZE / numQueues + 1));
                for (uint i = 0; i < numQueues; i++)
                {
                    uint uniformHashCode = checked(portion * i);
                    queueIds.Add(QueueId.GetQueueId(queueNamePrefix, i, uniformHashCode));
                }
            }
            this.hashRing = new HashRing<QueueId>(queueIds);
        }

        public IEnumerable<QueueId> GetQueuesForRange(IRingRange range)
        {
            foreach (QueueId queueId in hashRing.GetAllRingMembers())
                if (range.InRange(queueId.GetUniformHashCode()))
                    yield return queueId;
        }

        public IEnumerable<QueueId> GetAllQueues()
        {
            return hashRing.GetAllRingMembers();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters")]
        public QueueId GetQueueForStream(Guid streamGuid)
        {
            return hashRing.CalculateResponsible(streamGuid);
        }

        public override string ToString()
        {
            return hashRing.ToString();
        }
    }
}
