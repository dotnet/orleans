using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class SystemPlacement : PlacementStrategy
    {
        internal static SystemPlacement Singleton { get; } = new SystemPlacement();
        
        private SystemPlacement() {}

        public override bool Equals(object obj)
        {
            return obj is SystemPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
