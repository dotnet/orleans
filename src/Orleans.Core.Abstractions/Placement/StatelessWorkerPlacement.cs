using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class StatelessWorkerPlacement : PlacementStrategy
    {
        private static readonly int DefaultMaxStatelessWorkers = Environment.ProcessorCount;
        private const int DefaultMaxLocal = -1;
        public int MaxLocal { get; private set; }

        public StatelessWorkerPlacement()
            : this(DefaultMaxLocal)
        {
        }

        internal StatelessWorkerPlacement(int maxLocal)
        {
            // If maxLocal was not specified on the StatelessWorkerAttribute, 
            // we will use the defaultMaxStatelessWorkers, which is System.Environment.ProcessorCount.
            this.MaxLocal = maxLocal > 0 ? maxLocal : DefaultMaxStatelessWorkers;
        }

        public override string ToString()
        {
            return string.Format("StatelessWorkerPlacement(max={0})", this.MaxLocal);
        }
    }
}
