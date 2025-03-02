using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Metadata;
using Orleans.Runtime;

#nullable enable
namespace Orleans.Placement;

/// <summary>
/// Represents a strategy for filtering silos which a grain can be placed on.
/// </summary>
public abstract class PlacementFilterStrategy
{
    public int Order { get; private set; }

    protected PlacementFilterStrategy(int order)
    {
        Order = order;
    }

    /// <summary>
    /// Initializes an instance of this type using the provided grain properties.
    /// </summary>
    /// <param name="properties">
    /// The grain properties.
    /// </param>
    public void Initialize(GrainProperties properties)
    {
        var orderProperty = GetPlacementFilterGrainProperty("order", properties);
        if (!int.TryParse(orderProperty, out var parsedOrder))
        {
            throw new ArgumentException("Invalid order property value.");
        }

        Order = parsedOrder;

        AdditionalInitialize(properties);
    }

    public virtual void AdditionalInitialize(GrainProperties properties)
    {
    }

    /// <summary>
    /// Populates grain properties to specify the preferred placement strategy.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="grainClass">The grain class.</param>
    /// <param name="grainType">The grain type.</param>
    /// <param name="properties">The grain properties which will be populated by this method call.</param>
    public void PopulateGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
    {
        var typeName = GetType().Name;
        if (properties.TryGetValue(WellKnownGrainTypeProperties.PlacementFilter, out var existingValue))
        {
            properties[WellKnownGrainTypeProperties.PlacementFilter] = $"{existingValue},{typeName}";
        }
        else
        {
            properties[WellKnownGrainTypeProperties.PlacementFilter] = typeName;
        }

        properties[$"{WellKnownGrainTypeProperties.PlacementFilter}.{typeName}.order"] = Order.ToString(CultureInfo.InvariantCulture);

        foreach (var additionalGrainProperty in GetAdditionalGrainProperties(services, grainClass, grainType, properties))
        {
            properties[$"{WellKnownGrainTypeProperties.PlacementFilter}.{typeName}.{additionalGrainProperty.Key}"] = additionalGrainProperty.Value;
        }
    }

    protected string? GetPlacementFilterGrainProperty(string key, GrainProperties properties)
    {
        var typeName = GetType().Name;
        return properties.Properties.TryGetValue($"{WellKnownGrainTypeProperties.PlacementFilter}.{typeName}.{key}", out var value) ? value : null;
    }

    protected virtual IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, IReadOnlyDictionary<string, string> existingProperties)
        => Array.Empty<KeyValuePair<string, string>>();
}
