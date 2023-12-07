using Microsoft.Extensions.Configuration;

namespace Orleans.Providers;

/// <summary>
/// Interface for providers which configure Orleans services.
/// </summary>
/// <typeparam name="TBuilder">The type of the builder, such as <c>ISiloBuilder</c> or <c>IClientBuilder</c>.</typeparam>
public interface IProviderBuilder<TBuilder>
{
    /// <summary>
    /// Configures the provider.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="name">The provider name, or <see langword="null"/> if no name is specified.</param>
    /// <param name="configurationSection">The configuration section containing provider configuration.</param>
    void Configure(TBuilder builder, string name, IConfigurationSection configurationSection);
}
