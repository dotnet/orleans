using System;
using Orleans.Runtime;

namespace Orleans.Placement
{
    /// <summary>
    /// Marks a grain class as using the <c>RandomPlacement</c> policy.
    /// </summary>
    /// <remarks>
    /// This is the default placement policy, so this attribute does not need to be used for normal grains.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RandomPlacementAttribute : PlacementAttribute
    {
        public RandomPlacementAttribute() :
            base(RandomPlacement.Singleton)
        { }
    }
}