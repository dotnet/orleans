using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Streaming.NATS;
using Orleans.Streaming.NATS.Hosting;

[assembly: RegisterProvider("NATS", "Streaming", "Silo", typeof(NatsStreamProviderBuilder))]
[assembly: RegisterProvider("NATS", "Streaming", "Client", typeof(NatsStreamProviderBuilder))]
[assembly: RegisterProvider("Nats", "Streaming", "Silo", typeof(NatsStreamProviderBuilder))]
[assembly: RegisterProvider("Nats", "Streaming", "Client", typeof(NatsStreamProviderBuilder))]
[assembly: RegisterProvider("NatsServer", "Streaming", "Silo", typeof(NatsStreamProviderBuilder))]
[assembly: RegisterProvider("NatsServer", "Streaming", "Client", typeof(NatsStreamProviderBuilder))]

namespace Orleans.Hosting;

/// <summary>
/// Configures NATS JetStream streaming from Orleans provider configuration.
/// </summary>
public sealed class NatsStreamProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    /// <inheritdoc />
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(name);

        var partitionCount = GetInt32(
            configurationSection,
            nameof(NatsOptions.PartitionCount),
            defaultValue: 8,
            minimumValue: 1);

        builder.AddNatsStreams(name, streams =>
        {
            streams.ConfigureNats(GetOptionsBuilder(name, configurationSection));
            streams.ConfigurePartitioning(partitionCount);

            var cacheSize = GetNullableInt32(configurationSection, "CacheSize", minimumValue: 1);
            if (cacheSize is not null)
            {
                streams.ConfigureCache(cacheSize.Value);
            }
        });
    }

    /// <inheritdoc />
    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(name);

        var partitionCount = GetInt32(
            configurationSection,
            nameof(NatsOptions.PartitionCount),
            defaultValue: 8,
            minimumValue: 1);

        builder.AddNatsStreams(name, streams =>
        {
            streams.ConfigureNats(GetOptionsBuilder(name, configurationSection));
            streams.ConfigurePartitioning(partitionCount);
        });
    }

    private static Action<OptionsBuilder<NatsOptions>> GetOptionsBuilder(
        string providerName,
        IConfigurationSection configurationSection)
        => optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            options.StreamName = configurationSection[nameof(NatsOptions.StreamName)] ?? options.StreamName;
            options.BatchSize = GetInt32(
                configurationSection,
                nameof(NatsOptions.BatchSize),
                options.BatchSize,
                minimumValue: 1);
            options.PartitionCount = GetInt32(
                configurationSection,
                nameof(NatsOptions.PartitionCount),
                options.PartitionCount,
                minimumValue: 1);
            options.ProducerCount = GetInt32(
                configurationSection,
                nameof(NatsOptions.ProducerCount),
                options.ProducerCount,
                minimumValue: 1);
            options.NumReplicas = GetInt32(
                configurationSection,
                nameof(NatsOptions.NumReplicas),
                options.NumReplicas,
                minimumValue: 1);
            options.StorageType = GetEnum(
                configurationSection,
                nameof(NatsOptions.StorageType),
                options.StorageType);

            ConfigureConnection(providerName, configurationSection, options, services);
        });

    private static void ConfigureConnection(
        string providerName,
        IConfigurationSection configurationSection,
        NatsOptions options,
        IServiceProvider services)
    {
        var serviceKey = configurationSection["ServiceKey"];
        var connectionName = configurationSection["ConnectionName"];
        var connectionString = configurationSection["ConnectionString"];
        var url = configurationSection["Url"];

        var configuredSourceCount =
            (!string.IsNullOrWhiteSpace(serviceKey) ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(connectionName) ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(connectionString) ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(url) ? 1 : 0);

        if (configuredSourceCount == 0)
        {
            throw new OrleansConfigurationException(
                $"NATS stream provider '{providerName}' requires ServiceKey, ConnectionName, ConnectionString, or Url.");
        }

        if (configuredSourceCount > 1)
        {
            throw new OrleansConfigurationException(
                $"NATS stream provider '{providerName}' has ambiguous connection configuration. Configure exactly one of ServiceKey, ConnectionName, ConnectionString, or Url.");
        }

        if (!string.IsNullOrWhiteSpace(serviceKey))
        {
            options.Connection = services.GetKeyedService<INatsConnection>(serviceKey)
                ?? throw new OrleansConfigurationException(
                    $"NATS stream provider '{providerName}' requires the keyed INatsConnection '{serviceKey}'. Register it with AddKeyedNatsClient(\"{serviceKey}\").");
            options.NatsClientOptions = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(connectionName))
        {
            connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new OrleansConfigurationException(
                    $"NATS stream provider '{providerName}' references connection string '{connectionName}', but it has no configured value.");
            }
        }

        var connectionUrl = connectionString ?? url;
        if (!Uri.TryCreate(connectionUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "nats", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "tls", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            throw new OrleansConfigurationException(
                $"NATS stream provider '{providerName}' requires an absolute nats, tls, ws, or wss connection URI.");
        }

        options.Connection = null;
        options.NatsClientOptions = (options.NatsClientOptions ?? NatsOpts.Default) with { Url = connectionUrl };
    }

    private static int GetInt32(
        IConfigurationSection configurationSection,
        string key,
        int defaultValue,
        int minimumValue)
        => GetNullableInt32(configurationSection, key, minimumValue) ?? defaultValue;

    private static int? GetNullableInt32(
        IConfigurationSection configurationSection,
        string key,
        int minimumValue)
    {
        var value = configurationSection[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            || result < minimumValue)
        {
            throw new OrleansConfigurationException(
                $"NATS stream provider setting '{key}' must be an integer greater than or equal to {minimumValue}.");
        }

        return result;
    }

    private static TEnum GetEnum<TEnum>(
        IConfigurationSection configurationSection,
        string key,
        TEnum defaultValue)
        where TEnum : struct, Enum
    {
        var value = configurationSection[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result)
            || !Enum.IsDefined(result))
        {
            throw new OrleansConfigurationException(
                $"NATS stream provider setting '{key}' has invalid value '{value}'.");
        }

        return result;
    }
}
