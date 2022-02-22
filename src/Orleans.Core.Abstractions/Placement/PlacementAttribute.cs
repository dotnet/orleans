using System;
using System.Collections.Generic;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Placement
{
    /// <summary>
    /// Base for all placement policy marker attributes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public abstract class PlacementAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        public PlacementStrategy PlacementStrategy { get; private set; }

        protected PlacementAttribute(PlacementStrategy placement)
        {
            if (placement == null) throw new ArgumentNullException(nameof(placement));

            this.PlacementStrategy = placement;
        }

        /// <inheritdoc />
        public virtual void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            this.PlacementStrategy?.PopulateGrainProperties(services, grainClass, grainType, properties);
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
        /// <summary>
        /// Initializes a new instance of the <see cref="PreferLocalPlacementAttribute"/> class.
        /// </summary>
        public PreferLocalPlacementAttribute()
            : base(PreferLocalPlacement.Singleton)
        {
        }
    }

    /// <summary>
    /// Marks a grain class as using the <see cref="ActivationCountBasedPlacement"/> policy, which attempts to balance
    /// grain placment across servers based upon the relative number of recently active grains on each one.
    /// </summary>
    /// <remarks>
    /// See the documentation for <seealso cref="ActivationCountBasedPlacement"/> for more information.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class ActivationCountBasedPlacementAttribute : PlacementAttribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ActivationCountBasedPlacementAttribute"/> class.
        /// </summary>
        public ActivationCountBasedPlacementAttribute()
            : base(ActivationCountBasedPlacement.Singleton)
        {
        }
    }

    /// <summary>
    /// Marks a grain class as using the <c>SiloRoleBasedPlacement</c> policy.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class SiloRoleBasedPlacementAttribute : PlacementAttribute
    {
        public SiloRoleBasedPlacementAttribute() :
            base(SiloRoleBasedPlacement.Singleton)
        { }
    }
}
