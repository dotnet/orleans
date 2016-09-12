using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class ConsistentPartitionPlacement : PlacementStrategy
    {
        internal static ConsistentPartitionPlacement Singleton { get; private set; }

        internal static void InitializeClass()
        {
            Singleton = new ConsistentPartitionPlacement();
        }

        private ConsistentPartitionPlacement()
        {
        }

        public override bool Equals(object obj)
        {
            return obj is ConsistentPartitionPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}