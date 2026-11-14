using System;
using Newtonsoft.Json;

namespace Orleans.Persistence.MongoDB;

/// <summary>
/// Configures JSON settings used to produce MongoDB grain-state documents.
/// </summary>
public class JsonGrainStateSerializerOptions
{
    /// <summary>
    /// Gets or sets the callback applied to the Orleans JSON serializer settings.
    /// The default includes nulls and populates default values during deserialization.
    /// </summary>
    public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; } = ConfigureDefaultSettings;

    private static void ConfigureDefaultSettings(JsonSerializerSettings settings)
    {
        //// https://github.com/OrleansContrib/Orleans.Providers.MongoDB/issues/44
        //// Always include the default value, so that the deserialization process can overwrite default
        //// values that are not equal to the system defaults.
        settings.NullValueHandling = NullValueHandling.Include;
        settings.DefaultValueHandling = DefaultValueHandling.Populate;
    }
}
