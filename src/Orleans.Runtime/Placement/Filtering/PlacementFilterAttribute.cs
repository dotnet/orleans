using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// Base for all placement filter marker attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public abstract class PlacementFilterAttribute : Attribute, IGrainPropertiesProviderAttribute
{
    public PlacementFilterStrategy PlacementFilterStrategy { get; private set; }

    protected PlacementFilterAttribute(PlacementFilterStrategy placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        PlacementFilterStrategy = placement;
    }

    /// <inheritdoc />
    public virtual void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        => PlacementFilterStrategy?.PopulateGrainProperties(services, grainClass, grainType, properties);
}