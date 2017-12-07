using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class PreferLocalPlacement : PlacementStrategy
    {
        internal static PreferLocalPlacement Singleton { get; } = new PreferLocalPlacement();
        
        private PreferLocalPlacement()
        { }

        public override bool Equals(object obj)
        {
            return obj is PreferLocalPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
