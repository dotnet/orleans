using System;

namespace Orleans.Runtime
{
    [Serializable]
    public  class HashBasedPlacement : PlacementStrategy
    {
        internal bool SortSiloList { get; }

        internal static HashBasedPlacement Singleton { get; } = new HashBasedPlacement();

        internal HashBasedPlacement(bool sortSiloList)
        {
            this.SortSiloList = sortSiloList;
        }

        public HashBasedPlacement()
            : this(false)
        {
        }

        public override bool Equals(object obj)
        {
            return obj is HashBasedPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
