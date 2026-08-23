using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streaming.SQS;
using Orleans.Streaming.SQS.Streams;

[assembly: RegisterProvider("SQS", "Streaming", "Silo", typeof(SqsStreamProviderBuilder))]
[assembly: RegisterProvider("SQS", "Streaming", "Client", typeof(SqsStreamProviderBuilder))]
[assembly: RegisterProvider("AmazonSQS", "Streaming", "Silo", typeof(SqsStreamProviderBuilder))]
[assembly: RegisterProvider("AmazonSQS", "Streaming", "Client", typeof(SqsStreamProviderBuilder))]

namespace Orleans.Hosting;

/// <summary>
/// Configures Amazon SQS stream providers from Orleans configuration.
/// </summary>
public sealed class SqsStreamProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    /// <inheritdoc />
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(name);

        builder.AddSqsStreams(name, streams =>
        {
            ConfigureCommon(streams, name, configurationSection);

            var cacheSize = GetPositiveInt(configurationSection, "CacheSize");
            if (cacheSize.HasValue)
            {
                streams.ConfigureCache(cacheSize.Value);
            }
        });
    }

    /// <inheritdoc />
    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(name);

        builder.AddSqsStreams(name, streams => ConfigureCommon(streams, name, configurationSection));
    }

    private static void ConfigureCommon(
        SiloSqsStreamConfigurator streams,
        string providerName,
        IConfigurationSection configurationSection)
    {
        streams.ConfigureSqs(GetSqsOptionsBuilder(configurationSection));
        ConfigurePartitioning(streams.ConfigurePartitioning, configurationSection);
        ConfigureDataAdapter(streams.UseDataAdapter, providerName, configurationSection);
    }

    private static void ConfigureCommon(
        ClusterClientSqsStreamConfigurator streams,
        string providerName,
        IConfigurationSection configurationSection)
    {
        streams.ConfigureSqs(GetSqsOptionsBuilder(configurationSection));
        ConfigurePartitioning(streams.ConfigurePartitioning, configurationSection);
        ConfigureDataAdapter(streams.UseDataAdapter, providerName, configurationSection);
    }

    private static Action<OptionsBuilder<SqsOptions>> GetSqsOptionsBuilder(IConfigurationSection configurationSection)
        => optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) =>
        {
            options.ConnectionString = ResolveConnectionString(configurationSection, services);
            options.FifoQueue = GetBoolean(configurationSection, nameof(options.FifoQueue)) ?? options.FifoQueue;
            options.ReceiveWaitTimeSeconds = GetIntInRange(
                    configurationSection,
                    nameof(options.ReceiveWaitTimeSeconds),
                    minimum: 0,
                    maximum: 20)
                ?? options.ReceiveWaitTimeSeconds;
            options.VisibilityTimeoutSeconds = GetIntInRange(
                    configurationSection,
                    nameof(options.VisibilityTimeoutSeconds),
                    minimum: 0,
                    maximum: 43_200)
                ?? options.VisibilityTimeoutSeconds;

            ConfigureList(
                configurationSection.GetSection(nameof(options.ReceiveMessageAttributes)),
                options.ReceiveMessageAttributes);
            ConfigureList(
                configurationSection.GetSection(nameof(options.ReceiveMessageSystemAttributes)),
                options.ReceiveMessageSystemAttributes);
        });

    private static string ResolveConnectionString(
        IConfigurationSection configurationSection,
        IServiceProvider services)
    {
        var connectionString = configurationSection["ConnectionString"];
        var serviceKey = configurationSection["ServiceKey"];
        var connectionName = configurationSection["ConnectionName"];
        if (!string.IsNullOrWhiteSpace(serviceKey)
            && !string.IsNullOrWhiteSpace(connectionName)
            && !string.Equals(serviceKey, connectionName, StringComparison.Ordinal))
        {
            throw new OrleansConfigurationException(
                "SQS streaming configuration specifies different ServiceKey and ConnectionName values. Configure one referenced SQS service.");
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        var referenceName = !string.IsNullOrWhiteSpace(serviceKey) ? serviceKey : connectionName;
        if (!string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(referenceName))
        {
            throw new OrleansConfigurationException(
                "SQS streaming configuration specifies both a connection reference and ConnectionString. Configure one SQS connection source.");
        }

        if (!string.IsNullOrWhiteSpace(referenceName))
        {
            connectionString = configuration.GetConnectionString(referenceName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new OrleansConfigurationException(
                    $"SQS streaming connection reference '{referenceName}' did not resolve to a connection string.");
            }
        }

        var service = configurationSection["Service"];
        var region = configurationSection["Region"];
        var serviceEndpoint = GetFirstNonWhiteSpace(
            configurationSection["ServiceEndpoint"],
            configurationSection["Endpoint"]);
        var configuredLocations = new[] { service, region, serviceEndpoint }
            .Count(value => !string.IsNullOrWhiteSpace(value));
        if (configuredLocations > 1)
        {
            throw new OrleansConfigurationException(
                "SQS streaming configuration specifies multiple values among Service, Region, and ServiceEndpoint. Configure one SQS service location.");
        }

        if (!string.IsNullOrWhiteSpace(serviceEndpoint))
        {
            ValidateServiceEndpoint(serviceEndpoint);
        }

        var configuredService = GetFirstNonWhiteSpace(service, region, serviceEndpoint);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            if (!string.IsNullOrWhiteSpace(configuredService))
            {
                throw new OrleansConfigurationException(
                    "SQS streaming configuration specifies both a connection string and a service location. Configure one SQS connection source.");
            }

            ValidateConnectionString(connectionString);
            return connectionString;
        }

        if (string.IsNullOrWhiteSpace(configuredService))
        {
            var awsRegion = configuration["AWS_REGION"];
            var awsDefaultRegion = configuration["AWS_DEFAULT_REGION"];
            if (!string.IsNullOrWhiteSpace(awsRegion)
                && !string.IsNullOrWhiteSpace(awsDefaultRegion)
                && !string.Equals(awsRegion, awsDefaultRegion, StringComparison.OrdinalIgnoreCase))
            {
                throw new OrleansConfigurationException(
                    "SQS streaming configuration found different AWS_REGION and AWS_DEFAULT_REGION values. Configure one AWS region.");
            }

            configuredService = !string.IsNullOrWhiteSpace(awsRegion) ? awsRegion : awsDefaultRegion;
        }

        if (string.IsNullOrWhiteSpace(configuredService))
        {
            throw new OrleansConfigurationException(
                "SQS streaming requires a service location. Configure ServiceKey, ConnectionName, ConnectionString, Region, ServiceEndpoint, or AWS_REGION.");
        }

        ValidateServiceLocation(configuredService);
        return $"Service={configuredService}";
    }

    private static void ValidateConnectionString(string connectionString)
    {
        var properties = SqsConnectionString.Parse(connectionString);
        if (!properties.TryGetValue("Service", out var service))
        {
            throw new OrleansConfigurationException(
                "SQS streaming connection strings require a non-empty Service value containing an AWS region or SQS-compatible endpoint.");
        }

        ValidateServiceLocation(service);
    }

    private static void ValidateServiceLocation(string service)
    {
        if (service.Contains("://", StringComparison.Ordinal))
        {
            ValidateServiceEndpoint(service);
        }
    }

    private static void ValidateServiceEndpoint(string serviceEndpoint)
    {
        if (!Uri.TryCreate(serviceEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new OrleansConfigurationException(
                "SQS streaming ServiceEndpoint values must be absolute HTTP or HTTPS URIs.");
        }
    }

    private static void ConfigurePartitioning(
        Func<int, object> configurePartitioning,
        IConfigurationSection configurationSection)
    {
        var partitionCount = GetPositiveInt(configurationSection, "PartitionCount");
        if (partitionCount.HasValue)
        {
            configurePartitioning(partitionCount.Value);
        }
    }

    private static void ConfigureDataAdapter(
        Func<Func<IServiceProvider, string, ISQSDataAdapter>, object> useDataAdapter,
        string providerName,
        IConfigurationSection configurationSection)
    {
        var dataAdapterKey = configurationSection["DataAdapterKey"];
        var dataAdapterServiceKey = configurationSection["DataAdapterServiceKey"];
        if (!string.IsNullOrWhiteSpace(dataAdapterKey)
            && !string.IsNullOrWhiteSpace(dataAdapterServiceKey)
            && !string.Equals(dataAdapterKey, dataAdapterServiceKey, StringComparison.Ordinal))
        {
            throw new OrleansConfigurationException(
                "SQS streaming configuration specifies different DataAdapterKey and DataAdapterServiceKey values. Configure one keyed data adapter.");
        }

        var key = !string.IsNullOrWhiteSpace(dataAdapterKey) ? dataAdapterKey : dataAdapterServiceKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (string.Equals(key, providerName, StringComparison.Ordinal))
            {
                throw new OrleansConfigurationException(
                    "SQS streaming DataAdapterKey must differ from the stream provider name.");
            }

            useDataAdapter((services, _) => services.GetRequiredKeyedService<ISQSDataAdapter>(key));
        }
    }

    private static void ConfigureList(IConfigurationSection section, List<string> values)
    {
        var configuredValues = section.GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
        if (configuredValues.Count > 0)
        {
            values.Clear();
            values.AddRange(configuredValues);
        }
    }

    private static string? GetFirstNonWhiteSpace(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool? GetBoolean(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        throw new OrleansConfigurationException(
            $"SQS streaming configuration value '{key}' must be true or false.");
    }

    private static int? GetPositiveInt(IConfigurationSection section, string key)
    {
        var result = GetInt(section, key);
        if (result is <= 0)
        {
            throw new OrleansConfigurationException(
                $"SQS streaming configuration value '{key}' must be greater than zero.");
        }

        return result;
    }

    private static int? GetIntInRange(
        IConfigurationSection section,
        string key,
        int minimum,
        int maximum)
    {
        var result = GetInt(section, key);
        if (result is not null && (result < minimum || result > maximum))
        {
            throw new OrleansConfigurationException(
                $"SQS streaming configuration value '{key}' must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static int? GetInt(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var result))
        {
            return result;
        }

        throw new OrleansConfigurationException(
            $"SQS streaming configuration value '{key}' must be an integer.");
    }
}
