using System;
using Orleans.Runtime;

namespace Orleans.Placement
{
    /// <summary>
    /// Marks a grain class as using the <c>ActivationCountBasedPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class ActivationCountBasedPlacementAttribute : PlacementAttribute
    {
        public ActivationCountBasedPlacementAttribute() :
            base(ActivationCountBasedPlacement.Singleton)
        { }
    }
}