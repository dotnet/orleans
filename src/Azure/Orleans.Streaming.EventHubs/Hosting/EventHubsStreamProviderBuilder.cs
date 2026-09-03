using System;
using System.Text;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;

[assembly: RegisterProvider("EventHubs", "Streaming", "Silo", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("EventHubs", "Streaming", "Client", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("AzureEventHubs", "Streaming", "Silo", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("AzureEventHubs", "Streaming", "Client", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("AzureEventHub", "Streaming", "Silo", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("AzureEventHub", "Streaming", "Client", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("AzureEventHubConsumerGroup", "Streaming", "Silo", typeof(EventHubsStreamProviderBuilder))]
[assembly: RegisterProvider("AzureEventHubConsumerGroup", "Streaming", "Client", typeof(EventHubsStreamProviderBuilder))]

namespace Orleans.Hosting;

/// <summary>
/// Configures Azure Event Hubs stream providers from provider configuration.
/// </summary>
public sealed class EventHubsStreamProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    private const string EventHubNameConfigurationKey = "EventHubName";
    private const string ConsumerGroupConfigurationKey = "ConsumerGroup";
    private const string ConsumerGroupNameConfigurationKey = "ConsumerGroupName";

    /// <inheritdoc />
    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddEventHubStreams(name!, configurator =>
        {
            configurator.ConfigureEventHub(GetEventHubOptionsBuilder(configurationSection));
            configurator.UseAzureTableCheckpointer(GetEventHubCheckpointerOptionsBuilder(name!, configurationSection));
        });
    }

    /// <inheritdoc />
    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        builder.AddEventHubStreams(name!, configurator =>
            configurator.ConfigureEventHub(GetEventHubOptionsBuilder(configurationSection)));
    }

    private static Action<OptionsBuilder<EventHubOptions>> GetEventHubOptionsBuilder(IConfigurationSection configurationSection)
    {
        return optionsBuilder =>
        {
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var configuration = services.GetRequiredService<IConfiguration>();
                var serviceKey = configurationSection["ServiceKey"];
                var connectionName = configurationSection["ConnectionName"];
                var connectionString = configurationSection["ConnectionString"];
                var referenceName = !string.IsNullOrEmpty(serviceKey) ? serviceKey : connectionName;

                if (string.IsNullOrEmpty(connectionString) && !string.IsNullOrEmpty(referenceName))
                {
                    connectionString = configuration.GetConnectionString(referenceName);
                }

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new OrleansConfigurationException(
                        "Event Hubs streaming requires a connection string. Configure ServiceKey, ConnectionName, or ConnectionString.");
                }

                var eventHubName = configurationSection[EventHubNameConfigurationKey]
                    ?? GetAspireReferenceProperty(configuration, referenceName, EventHubNameConfigurationKey)
                    ?? GetConnectionProperty(connectionString, "EntityPath");
                if (string.IsNullOrWhiteSpace(eventHubName))
                {
                    throw new OrleansConfigurationException(
                        "Event Hubs streaming requires EventHubName in the provider or referenced Aspire resource configuration.");
                }

                var consumerGroup = configurationSection[ConsumerGroupConfigurationKey]
                    ?? configurationSection[ConsumerGroupNameConfigurationKey]
                    ?? GetAspireReferenceProperty(configuration, referenceName, ConsumerGroupConfigurationKey)
                    ?? GetAspireReferenceProperty(configuration, referenceName, ConsumerGroupNameConfigurationKey)
                    ?? GetConnectionProperty(connectionString, ConsumerGroupConfigurationKey)
                    ?? EventHubConsumerClient.DefaultConsumerGroupName;

                var normalizedConnectionString = RemoveConnectionProperty(
                    RemoveConnectionProperty(connectionString, ConsumerGroupConfigurationKey),
                    "EntityPath");
                var fullyQualifiedNamespace = configurationSection["FullyQualifiedNamespace"]
                    ?? GetAspireReferenceProperty(configuration, referenceName, "Host")
                    ?? GetConnectionHost(connectionString);
                if (!HasSharedAccessCredential(normalizedConnectionString)
                    && !string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
                {
                    options.ConfigureEventHubConnection(
                        fullyQualifiedNamespace,
                        eventHubName,
                        consumerGroup,
                        new DefaultAzureCredential());
                }
                else
                {
                    options.ConfigureEventHubConnection(normalizedConnectionString, eventHubName, consumerGroup);
                }
            });
        };
    }

    private static Action<OptionsBuilder<AzureTableStreamCheckpointerOptions>> GetEventHubCheckpointerOptionsBuilder(string name, IConfigurationSection configurationSection)
    {
        return optionsBuilder =>
        {
            optionsBuilder.Configure<IServiceProvider>((options, services) =>
            {
                var serviceKey = configurationSection["CheckpointerServiceKey"];
                if (!string.IsNullOrEmpty(serviceKey))
                {
                    options.TableServiceClient = services.GetRequiredKeyedService<TableServiceClient>(serviceKey);
                    return;
                }

                var connectionName = configurationSection["CheckpointerConnectionName"];
                var connectionString = configurationSection["CheckpointerConnectionString"];
                if (!string.IsNullOrEmpty(connectionName) && string.IsNullOrEmpty(connectionString))
                {
                    connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connectionName);
                }

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new OrleansConfigurationException(
                        $"Event Hubs stream provider '{name}' requires CheckpointerServiceKey, CheckpointerConnectionName, or CheckpointerConnectionString.");
                }

                if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                {
                    options.TableServiceClient = string.IsNullOrEmpty(uri.Query)
                        ? new TableServiceClient(uri, new DefaultAzureCredential())
                        : new TableServiceClient(uri);
                }
                else
                {
                    options.TableServiceClient = new TableServiceClient(connectionString);
                }
            });
        };
    }

    private static bool HasSharedAccessCredential(string connectionString)
        => connectionString.Contains("SharedAccessKey=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase);

    private static string? GetAspireReferenceProperty(
        IConfiguration configuration,
        string? referenceName,
        string propertyName)
    {
        if (string.IsNullOrEmpty(referenceName))
        {
            return null;
        }

        return configuration[$"{EncodeEnvironmentVariableName(referenceName)}_{propertyName.ToUpperInvariant()}"];
    }

    private static string EncodeEnvironmentVariableName(string name)
    {
        var builder = new StringBuilder(name.Length + 1);
        if (char.IsAsciiDigit(name[0]))
        {
            builder.Append('_');
        }

        foreach (var character in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character)
                ? char.ToUpperInvariant(character)
                : '_');
        }

        return builder.ToString();
    }

    private static string? GetConnectionHost(string connectionString)
    {
        var endpoint = GetConnectionProperty(connectionString, "Endpoint");
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string? GetConnectionProperty(string connectionString, string propertyName)
    {
        var prefix = propertyName + "=";
        return connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .FirstOrDefault(segment => segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private static string RemoveConnectionProperty(string connectionString, string propertyName)
    {
        var prefix = propertyName + "=";
        return string.Join(
            ';',
            connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => !segment.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }
}
