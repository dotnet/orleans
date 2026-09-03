using System;
using System.Collections.Generic;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Placement;

/// <summary>
/// Base for all placement filter marker attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public abstract class PlacementFilterAttribute : Attribute, IGrainPropertiesProviderAttribute
{
    /// <summary>
    /// Gets the placement filter strategy.
    /// </summary>
    public PlacementFilterStrategy PlacementFilterStrategy { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlacementFilterAttribute"/> class.
    /// </summary>
    /// <param name="placement">The placement filter strategy associated with this attribute.</param>
    protected PlacementFilterAttribute(PlacementFilterStrategy placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        PlacementFilterStrategy = placement;
    }

    /// <inheritdoc />
    public virtual void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        => PlacementFilterStrategy?.PopulateGrainProperties(services, grainClass, grainType, properties);
}
