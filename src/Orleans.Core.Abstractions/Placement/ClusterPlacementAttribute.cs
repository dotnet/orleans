using System;
using System.Collections.Generic;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Placement;

/// <summary>
/// Specifies the cluster locator used by a grain class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ClusterLocatorAttribute : Attribute, IGrainPropertiesProviderAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterLocatorAttribute"/> class.
    /// </summary>
    public ClusterLocatorAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>
    /// Gets the cluster locator name.
    /// </summary>
    public string Name { get; }

    /// <inheritdoc/>
    public void Populate(
        IServiceProvider services,
        Type grainClass,
        GrainType grainType,
        Dictionary<string, string> properties)
        => properties[WellKnownGrainTypeProperties.ClusterLocator] = Name;
}

/// <summary>
/// Base class for cluster placement marker attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public abstract class ClusterPlacementAttribute : Attribute, IGrainPropertiesProviderAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterPlacementAttribute"/> class.
    /// </summary>
    protected ClusterPlacementAttribute(ClusterPlacementStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        Strategy = strategy;
    }

    /// <summary>
    /// Gets the cluster placement strategy.
    /// </summary>
    public ClusterPlacementStrategy Strategy { get; }

    /// <inheritdoc/>
    public void Populate(
        IServiceProvider services,
        Type grainClass,
        GrainType grainType,
        Dictionary<string, string> properties)
        => Strategy.PopulateGrainProperties(services, grainClass, grainType, properties);
}
