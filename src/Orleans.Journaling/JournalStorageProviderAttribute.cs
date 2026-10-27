using Orleans.Metadata;

namespace Orleans.Journaling;

/// <summary>
/// Selects the named journal storage provider used by a grain type's activation-scoped durable state manager.
/// </summary>
/// <param name="providerName">The registered journal storage provider name.</param>
/// <remarks>
/// All durable state in an activation shares the selected provider. Grain types without this attribute use
/// <see cref="Providers.ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME"/>.
/// Recovery reads the selected provider's physical namespace. Changing the selection for existing journals
/// requires a data migration or cutover strategy.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class JournalStorageProviderAttribute(string providerName) : Attribute, IGrainPropertiesProviderAttribute
{
    internal const string PropertyKey = "journal-storage-provider";

    /// <summary>
    /// Gets the registered journal storage provider name.
    /// </summary>
    public string ProviderName { get; } = providerName;

    /// <inheritdoc />
    /// <exception cref="OrleansConfigurationException">The provider name is null, empty, or whitespace.</exception>
    public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
    {
        if (string.IsNullOrWhiteSpace(ProviderName))
        {
            throw new OrleansConfigurationException(
                $"Journal storage provider '{ProviderName}' for grain type '{grainClass.FullName}' ('{grainType}') must have a non-empty name.");
        }

        properties[PropertyKey] = ProviderName;
    }
}
