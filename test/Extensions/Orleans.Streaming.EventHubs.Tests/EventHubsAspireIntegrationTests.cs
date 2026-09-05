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
        var configuration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: IsRelevantEnvironmentVariable);
        var clientConfiguration = await AspireResourceConfiguration.CreateAsync(
            client.Resource,
            app.Services,
            include: IsRelevantEnvironmentVariable);

        Assert.Equal(expectedProviderType, configuration[$"Orleans:Streaming:{providerName}:ProviderType"]);
        Assert.Equal(serviceKey, configuration[$"Orleans:Streaming:{providerName}:ServiceKey"]);
        Assert.Equal(expectedProviderType, clientConfiguration[$"Orleans:Streaming:{providerName}:ProviderType"]);
        Assert.Equal(serviceKey, clientConfiguration[$"Orleans:Streaming:{providerName}:ServiceKey"]);
        Assert.Equal(eventHubName, configuration[$"{serviceKey.ToUpperInvariant().Replace('-', '_')}_EVENTHUBNAME"]);
        if (useConsumerGroupResource)
        {
            Assert.Equal(consumerGroupName, configuration["ORDERS_CONSUMER_CONSUMERGROUPNAME"]);
        }
        else
        {
            Assert.Null(configuration["ORDERS_HUB_CONSUMERGROUPNAME"]);
        }

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
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
        clientHostBuilder.Configuration.AddConfiguration(clientConfiguration);
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
                TestDefaultConfiguration.DataConnectionString)
            .WithEnvironment(
                "ConnectionStrings__orders-consumer",
                TestDefaultConfiguration.EventHubConnectionString);

        var emulatorEndpoint = eventHubs.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(endpoint => endpoint.Name == "emulator");
        emulatorEndpoint.AllocatedEndpoint = new AllocatedEndpoint(emulatorEndpoint, "localhost", 5672);

        await using var app = await builder.BuildAsync(TestContext.Current.CancellationToken);
        var configuration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: IsRelevantEnvironmentVariable);

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
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
        Assert.NotEmpty(await producer.GetPartitionIdsAsync(TestContext.Current.CancellationToken));
        await checkpointerOptions.TableServiceClient!.GetPropertiesAsync(TestContext.Current.CancellationToken);
    }

    private static bool IsRelevantEnvironmentVariable(string name)
        => name.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
            || name.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
            || name.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
            || name.StartsWith("ORDERS_", StringComparison.Ordinal);
}
