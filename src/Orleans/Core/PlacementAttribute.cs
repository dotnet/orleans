using System;
using Orleans.Runtime;

namespace Orleans.Placement
{
    /// <summary>
    /// Base for all placement policy marker attributes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public abstract class PlacementAttribute : Attribute
    {
        internal PlacementStrategy PlacementStrategy { get; private set; }

        internal PlacementAttribute(PlacementStrategy placement)
        {
            if (placement == null) throw new ArgumentNullException(nameof(placement));

            PlacementStrategy = placement;
        }
    }
}