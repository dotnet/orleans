using System;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Connections.Security;
using Orleans.Connections.Security.Entra;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring Microsoft Entra silo connection authentication.
/// </summary>
public static class EntraSiloConnectionAuthenticationExtensions
{
    /// <summary>
    /// Configures Microsoft Entra token acquisition and validation for authenticated silo connections.
    /// </summary>
    /// <param name="builder">The authenticated silo connection builder.</param>
    /// <param name="credential">The caller-supplied credential used to acquire tokens.</param>
    /// <param name="configureOptions">Configures Microsoft Entra token acquisition and validation.</param>
    /// <returns>The builder.</returns>
    public static SiloConnectionAuthenticationBuilder UseEntra(
        this SiloConnectionAuthenticationBuilder builder,
        TokenCredential credential,
        Action<EntraSiloConnectionOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var services = builder.Services;
        var optionsName = builder.Name;
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<EntraSiloConnectionOptions>, EntraSiloConnectionOptionsValidator>());
        services.AddOptions<EntraSiloConnectionOptions>(optionsName)
            .Configure(configureOptions)
            .ValidateOnStart();
        return builder
            .UseTokenProvider(serviceProvider =>
            {
                var options = serviceProvider
                    .GetRequiredService<IOptionsMonitor<EntraSiloConnectionOptions>>()
                    .Get(optionsName);
                return new EntraSiloConnectionTokenProvider(
                    credential,
                    options,
                    builder.TimeProvider);
            })
            .UseTokenValidator(serviceProvider =>
            {
                var options = serviceProvider
                    .GetRequiredService<IOptionsMonitor<EntraSiloConnectionOptions>>()
                    .Get(optionsName);
                var metadata = new EntraOpenIdConfigurationProvider(options, builder.TimeProvider);
                var validator = new EntraJwtValidator(
                    options,
                    metadata,
                    builder.TimeProvider);
                return new EntraSiloConnectionTokenValidator(validator, metadata);
            });
    }
}
