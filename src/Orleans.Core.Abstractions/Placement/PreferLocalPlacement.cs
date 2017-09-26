using System;

namespace Orleans.Runtime
{
    [Serializable]
    public class PreferLocalPlacement : PlacementStrategy
    {
        public static PreferLocalPlacement Singleton { get; } = new PreferLocalPlacement();
        
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
