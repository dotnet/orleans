using Orleans.Providers;

namespace Orleans.EventSourcing.CustomStorage;

/// <summary>
/// Selects the named <see cref="ICustomStorageFactory"/> used to create custom storage for a grain.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CustomStorageProviderAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the registered factory name.
    /// </summary>
    public string ProviderName { get; set; } = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
}
