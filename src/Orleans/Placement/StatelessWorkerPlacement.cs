using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class StatelessWorkerPlacement : PlacementStrategy
    {
        private static readonly int defaultMaxStatelessWorkers = Environment.ProcessorCount;

        public int MaxLocal { get; private set; }
        
        internal StatelessWorkerPlacement(int maxLocal = -1)
        {
            // If maxLocal was not specified on the StatelessWorkerAttribute, 
            // we will use the defaultMaxStatelessWorkers, which is System.Environment.ProcessorCount.
            MaxLocal = maxLocal > 0 ? maxLocal : defaultMaxStatelessWorkers;
        }

        public override string ToString()
        {
            return String.Format("StatelessWorkerPlacement(max={0})", MaxLocal);
        }

        public override bool Equals(object obj)
        {
            var other = obj as StatelessWorkerPlacement;
            return other != null && MaxLocal == other.MaxLocal;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode() ^ MaxLocal.GetHashCode();
        }
    }

}
