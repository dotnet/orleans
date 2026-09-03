using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Placement;

/// <summary>
/// Represents a strategy for filtering silos which a grain can be placed on.
/// </summary>
public abstract class PlacementFilterStrategy
{
    /// <summary>
    /// Gets the order in which this filter is applied relative to other placement filters.
    /// </summary>
    public int Order { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlacementFilterStrategy"/> class.
    /// </summary>
    /// <param name="order">The order in which this filter is applied relative to other placement filters.</param>
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

    /// <summary>
    /// Initializes strategy-specific state using the provided grain properties.
    /// </summary>
    /// <param name="properties">The grain properties.</param>
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

    /// <summary>
    /// Gets a property for this placement filter strategy.
    /// </summary>
    /// <param name="key">The strategy-specific property key.</param>
    /// <param name="properties">The grain properties.</param>
    /// <returns>The property value, or <see langword="null"/> if the property is not present.</returns>
    protected string? GetPlacementFilterGrainProperty(string key, GrainProperties properties)
    {
        var typeName = GetType().Name;
        return properties.Properties.TryGetValue($"{WellKnownGrainTypeProperties.PlacementFilter}.{typeName}.{key}", out var value) ? value : null;
    }

    /// <summary>
    /// Gets the strategy-specific grain properties to populate.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="grainClass">The grain class.</param>
    /// <param name="grainType">The grain type.</param>
    /// <param name="existingProperties">The grain properties populated so far.</param>
    /// <returns>The strategy-specific grain properties.</returns>
    protected virtual IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, IReadOnlyDictionary<string, string> existingProperties)
        => Array.Empty<KeyValuePair<string, string>>();
}
