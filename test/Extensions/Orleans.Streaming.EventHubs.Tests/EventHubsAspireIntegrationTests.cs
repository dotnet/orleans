using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Tester;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class EventHubsAspireIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AspireAppModel_ProducesWorkingOrleansSiloConfiguration(bool useConsumerGroupResource)
    {
        const string providerName = "orders-stream";
        const string eventHubName = "orders";
        const string consumerGroupName = "workers";

        await using var builder = DistributedApplicationTestingBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("event-hubs").RunAsEmulator();
        var eventHub = eventHubs.AddHub("orders-hub", eventHubName);
        var orleans = builder.AddOrleans("cluster").WithDevelopmentClustering();
        var serviceKey = "orders-hub";
        var expectedProviderType = "AzureEventHub";
        var expectedConsumerGroup = EventHubConsumerClient.DefaultConsumerGroupName;
        if (useConsumerGroupResource)
        {
            var consumerGroup = eventHub.AddConsumerGroup("orders-consumer", consumerGroupName);
            orleans.WithStreaming(providerName, consumerGroup);
            serviceKey = "orders-consumer";
            expectedProviderType = "AzureEventHubConsumerGroup";
            expectedConsumerGroup = consumerGroupName;
        }
        else
        {
            orleans.WithStreaming(providerName, eventHub);
        }

        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment(
                $"Orleans__Streaming__{providerName}__CheckpointerConnectionString",
                "UseDevelopmentStorage=true");
        var client = builder.AddContainer("client", "unused")
            .WithReference(orleans.AsClient());

        var emulatorEndpoint = eventHubs.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(endpoint => endpoint.Name == "emulator");
        emulatorEndpoint.AllocatedEndpoint = new AllocatedEndpoint(emulatorEndpoint, "localhost", 5672);

        await using var app = await builder.BuildAsync(TestContext.Current.CancellationToken);
        var environment = await GetEnvironmentVariablesAsync(silo.Resource, app.Services);
        var clientEnvironment = await GetEnvironmentVariablesAsync(client.Resource, app.Services);

        Assert.Equal(expectedProviderType, environment[$"Orleans:Streaming:{providerName}:ProviderType"]);
        Assert.Equal(serviceKey, environment[$"Orleans:Streaming:{providerName}:ServiceKey"]);
        Assert.Equal(expectedProviderType, clientEnvironment[$"Orleans:Streaming:{providerName}:ProviderType"]);
        Assert.Equal(serviceKey, clientEnvironment[$"Orleans:Streaming:{providerName}:ServiceKey"]);
        Assert.Equal(eventHubName, environment[$"{serviceKey.ToUpperInvariant().Replace('-', '_')}_EVENTHUBNAME"]);
        if (useConsumerGroupResource)
        {
            Assert.Equal(consumerGroupName, environment["ORDERS_CONSUMER_CONSUMERGROUPNAME"]);
        }
        else
        {
            Assert.DoesNotContain("ORDERS_HUB_CONSUMERGROUPNAME", environment);
        }

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(environment);
        hostBuilder.UseOrleans();

        using var host = hostBuilder.Build();
        var eventHubOptions = host.Services
            .GetRequiredService<IOptionsMonitor<EventHubOptions>>()
            .Get(providerName);
        var checkpointerOptions = host.Services
            .GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>()
            .Get(providerName);

        Assert.Equal(eventHubName, eventHubOptions.EventHubName);
        Assert.Equal(expectedConsumerGroup, eventHubOptions.ConsumerGroup);
        await using var connection = eventHubOptions.CreateConnection(new EventHubConnectionOptions());
        Assert.Equal("localhost", connection.FullyQualifiedNamespace);
        Assert.Equal(eventHubName, connection.EventHubName);
        Assert.NotNull(checkpointerOptions.TableServiceClient);
        Assert.Equal("devstoreaccount1", checkpointerOptions.TableServiceClient.AccountName);

        var clientHostBuilder = Host.CreateApplicationBuilder();
        clientHostBuilder.Configuration.AddInMemoryCollection(clientEnvironment);
        clientHostBuilder.UseOrleansClient();

        using var clientHost = clientHostBuilder.Build();
        var clientOptions = clientHost.Services
            .GetRequiredService<IOptionsMonitor<EventHubOptions>>()
            .Get(providerName);
        Assert.Equal(eventHubName, clientOptions.EventHubName);
        Assert.Equal(expectedConsumerGroup, clientOptions.ConsumerGroup);
    }

    [Fact]
    public async Task AspireConfiguration_ConnectsToLiveEventHubsAndCheckpointer()
    {
        TestUtils.CheckForEventHub();
        TestUtils.CheckForAzureStorage();
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            throw Xunit.Sdk.SkipException.ForSkip("This test exercises the connection-string configuration used by the emulator CI job.");
        }

        const string providerName = "orders-stream";
        const string eventHubName = "ehorleanstest";
        const string consumerGroupName = "orleansnightly";
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var eventHubs = builder.AddAzureEventHubs("event-hubs").RunAsEmulator();
        var consumerGroup = eventHubs
            .AddHub("orders-hub", eventHubName)
            .AddConsumerGroup("orders-consumer", consumerGroupName);
        var orleans = builder.AddOrleans("cluster")
            .WithDevelopmentClustering()
            .WithStreaming(providerName, consumerGroup);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment(
                $"Orleans__Streaming__{providerName}__CheckpointerConnectionString",
                TestDefaultConfiguration.DataConnectionString);

        var emulatorEndpoint = eventHubs.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(endpoint => endpoint.Name == "emulator");
        emulatorEndpoint.AllocatedEndpoint = new AllocatedEndpoint(emulatorEndpoint, "localhost", 5672);

        await using var app = await builder.BuildAsync();
        var environment = await GetEnvironmentVariablesAsync(silo.Resource, app.Services);
        environment["ConnectionStrings:orders-consumer"] = TestDefaultConfiguration.EventHubConnectionString;

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(environment);
        hostBuilder.UseOrleans();

        using var host = hostBuilder.Build();
        var eventHubOptions = host.Services
            .GetRequiredService<IOptionsMonitor<EventHubOptions>>()
            .Get(providerName);
        var checkpointerOptions = host.Services
            .GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>()
            .Get(providerName);

        await using var connection = eventHubOptions.CreateConnection(new EventHubConnectionOptions());
        await using var producer = new EventHubProducerClient(connection);
        Assert.NotEmpty(await producer.GetPartitionIdsAsync());
        await checkpointerOptions.TableServiceClient!.GetPropertiesAsync();
    }

    private static async Task<Dictionary<string, string?>> GetEnvironmentVariablesAsync(
        IResource resource,
        IServiceProvider services)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
            {
                ServiceProvider = services,
            });
        var values = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(executionContext, resource, values);

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(callbackContext);
        }

        var valueContext = new ValueProviderContext
        {
            Caller = resource,
            ExecutionContext = executionContext,
            Network = KnownNetworkIdentifiers.LocalhostNetwork,
        };
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            if (!IsRelevantEnvironmentVariable(key))
            {
                continue;
            }

            var normalizedKey = key.StartsWith("Orleans__", StringComparison.Ordinal)
                || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
                    ? key.Replace("__", ":", StringComparison.Ordinal)
                    : key;
            result[normalizedKey] = value switch
            {
                IValueProvider provider => await provider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return result;
    }

    private static bool IsRelevantEnvironmentVariable(string name)
        => name.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
            || name.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
            || name.StartsWith("ORDERS_", StringComparison.Ordinal);
}
