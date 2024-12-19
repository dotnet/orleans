using System;
using System.Collections.Generic;
using Orleans.Metadata;

namespace Orleans.Runtime.Placement.Filtering;

public abstract class PlacementFilterStrategy
{
    /// <summary>
    /// Initializes an instance of this type using the provided grain properties.
    /// </summary>
    /// <param name="properties">
    /// The grain properties.
    /// </param>
    public virtual void Initialize(GrainProperties properties)
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

        foreach (var additionalGrainProperty in GetAdditionalGrainProperties(services, grainClass, grainType, properties))
        {
            properties[$"{WellKnownGrainTypeProperties.PlacementFilter}.{typeName}.{additionalGrainProperty.Key}"] = additionalGrainProperty.Value;
        }
    }

    protected string GetPlacementFilterGrainProperty(string key, GrainProperties properties)
    {
        var typeName = GetType().Name;
        return properties.Properties.TryGetValue($"{WellKnownGrainTypeProperties.PlacementFilter}.{typeName}.{key}", out var value) ? value : null;
    }

    protected virtual IEnumerable<KeyValuePair<string, string>> GetAdditionalGrainProperties(IServiceProvider services, Type grainClass, GrainType grainType, IReadOnlyDictionary<string, string> existingProperties)
        => Array.Empty<KeyValuePair<string, string>>();
}
