
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Streams
{
    /// <summary>
    /// A <see cref="IQueueFlowController"/> which aggregates multiple other <see cref="IQueueFlowController"/> values.
    /// </summary>
    public class AggregatedQueueFlowController : List<IQueueFlowController>, IQueueFlowController
    {
        private readonly int defaultMaxAddCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="AggregatedQueueFlowController"/> class.
        /// </summary>
        /// <param name="defaultMaxAddCount">The default maximum add count, see <see cref="IQueueFlowController.GetMaxAddCount"/>.</param>
        public AggregatedQueueFlowController(int defaultMaxAddCount)
        {
            this.defaultMaxAddCount = defaultMaxAddCount;
        }

        /// <inheritdoc/>
        public int GetMaxAddCount()
        {
            return this.Aggregate(defaultMaxAddCount, (count, fc) => Math.Min(count, fc.GetMaxAddCount()));
        }
    }
}
