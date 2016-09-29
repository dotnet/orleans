using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class ActivationCountBasedPlacement : PlacementStrategy
    {
        internal static ActivationCountBasedPlacement Singleton { get; private set; }

        private ActivationCountBasedPlacement()
        {}

        internal static void InitializeClass()
        {
            Singleton = new ActivationCountBasedPlacement();
        }

        public override bool Equals(object obj)
        {
            return obj is ActivationCountBasedPlacement;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }
    }
}
