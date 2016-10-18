
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Streams
{
    public class AggregatedQueueFlowController : List<IQueueFlowController>, IQueueFlowController
    {
        private readonly int defaultMaxAddCount;

        public AggregatedQueueFlowController(int defaultMaxAddCount)
        {
            this.defaultMaxAddCount = defaultMaxAddCount;
        }

        public int GetMaxAddCount()
        {
            return this.Aggregate(defaultMaxAddCount, (count, fc) => Math.Min(count, fc.GetMaxAddCount()));
        }
    }
}
