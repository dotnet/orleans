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
        public PlacementStrategy PlacementStrategy { get; private set; }

        protected PlacementAttribute(PlacementStrategy placement)
        {
            if (placement == null) throw new ArgumentNullException(nameof(placement));

            this.PlacementStrategy = placement;
        }
    }

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
        {
        }
    }

    /// <summary>
    /// Marks a grain class as using the <c>HashBasedPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class HashBasedPlacementAttribute : PlacementAttribute
    {
        public HashBasedPlacementAttribute() :
            base(HashBasedPlacement.Singleton)
        { }
    }

    /// <summary>
    /// Marks a grain class as using the <c>PreferLocalPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class PreferLocalPlacementAttribute : PlacementAttribute
    {
        public PreferLocalPlacementAttribute() :
            base(PreferLocalPlacement.Singleton)
        {
        }
    }

    /// <summary>
    /// Marks a grain class as using the <c>ActivationCountBasedPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class ActivationCountBasedPlacementAttribute : PlacementAttribute
    {
        public ActivationCountBasedPlacementAttribute() :
            base(ActivationCountBasedPlacement.Singleton)
        {
        }
    }
}
