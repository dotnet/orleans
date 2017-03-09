using System;
using Orleans.Runtime;

namespace Orleans.Placement
{
    /// <summary>
    /// Marks a grain class as using the <c>PreferLocalPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false) ]
    public sealed class PreferLocalPlacementAttribute : PlacementAttribute
    {
        public PreferLocalPlacementAttribute() :
            base(PreferLocalPlacement.Singleton)
        { }
    }
}