using System.Reflection;
using Azure.Data.Tables;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class EventHubsStreamProviderBuilderTests
{
    [Fact]
    public void ConfigureSilo_AspireConsumerGroupReference_ConfiguresEventHubAndCheckpointer()
    {
        const string providerName = "orders-stream";
        const string eventHubServiceKey = "orders-consumer";
        const string checkpointerServiceKey = "checkpoints";
        var checkpointer = new TableServiceClient("UseDevelopmentStorage=true");
        var builder = CreateSiloBuilder(
            ($"Orleans:Streaming:{providerName}:ServiceKey", eventHubServiceKey),
            ($"Orleans:Streaming:{providerName}:CheckpointerServiceKey", checkpointerServiceKey),
            ($"ConnectionStrings:{eventHubServiceKey}", CreateEventHubsConnectionString("orders", "workers")),
            ("ORDERS_CONSUMER_EVENTHUBNAME", "orders"),
            ("ORDERS_CONSUMER_CONSUMERGROUPNAME", "workers"));
        builder.Services.AddKeyedSingleton(checkpointerServiceKey, checkpointer);

        ConfigureSilo(builder, providerName);

        using var services = builder.Services.BuildServiceProvider();
        var eventHubOptions = services.GetRequiredService<IOptionsMonitor<EventHubOptions>>().Get(providerName);
        var checkpointerOptions = services.GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>().Get(providerName);
        Assert.Equal("orders", eventHubOptions.EventHubName);
        Assert.Equal("workers", eventHubOptions.ConsumerGroup);
        Assert.Same(checkpointer, checkpointerOptions.TableServiceClient);
    }

    [Fact]
    public async Task ConfigureClient_ProviderNameCanDifferFromAspireResourceName()
    {
        const string providerName = "client-stream";
        const string eventHubServiceKey = "telemetry-consumer";
        var builder = CreateClientBuilder(
            ($"Orleans:Streaming:{providerName}:ServiceKey", eventHubServiceKey),
            ($"ConnectionStrings:{eventHubServiceKey}", CreateEventHubsConnectionString("telemetry", "dashboard")),
            ("TELEMETRY_CONSUMER_EVENTHUBNAME", "telemetry"),
            ("TELEMETRY_CONSUMER_CONSUMERGROUPNAME", "dashboard"));

        new EventHubsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<EventHubOptions>>().Get(providerName);
        Assert.Equal("telemetry", options.EventHubName);
        Assert.Equal("dashboard", options.ConsumerGroup);
        await using var connection = options.CreateConnection(new EventHubConnectionOptions());
        Assert.Equal("telemetry", connection.EventHubName);
    }

    [Fact]
    public void ConfigureClient_EventHubReference_UsesDefaultConsumerGroup()
    {
        const string providerName = "client-stream";
        const string eventHubServiceKey = "telemetry";
        var builder = CreateClientBuilder(
            ($"Orleans:Streaming:{providerName}:ServiceKey", eventHubServiceKey),
            ($"ConnectionStrings:{eventHubServiceKey}", CreateEventHubConnectionString("telemetry")),
            ("TELEMETRY_EVENTHUBNAME", "telemetry"));

        new EventHubsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<EventHubOptions>>().Get(providerName);
        Assert.Equal(EventHubConsumerClient.DefaultConsumerGroupName, options.ConsumerGroup);
    }

    [Fact]
    public async Task ConfigureClient_PasswordlessAspireReference_UsesInjectedHost()
    {
        const string providerName = "client-stream";
        const string eventHubServiceKey = "telemetry";
        var builder = CreateClientBuilder(
            ($"Orleans:Streaming:{providerName}:ServiceKey", eventHubServiceKey),
            ($"ConnectionStrings:{eventHubServiceKey}", "Endpoint=https://telemetry.servicebus.windows.net;EntityPath=events"),
            ("TELEMETRY_HOST", "telemetry.servicebus.windows.net"),
            ("TELEMETRY_EVENTHUBNAME", "events"));

        new EventHubsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<EventHubOptions>>().Get(providerName);
        await using var connection = options.CreateConnection(new EventHubConnectionOptions());
        Assert.Equal("telemetry.servicebus.windows.net", connection.FullyQualifiedNamespace);
        Assert.Equal("events", connection.EventHubName);
    }

    [Fact]
    public void ConfigureSilo_MissingCheckpointerConfiguration_ReportsRequiredSettings()
    {
        const string providerName = "orders-stream";
        const string eventHubServiceKey = "orders-consumer";
        var builder = CreateSiloBuilder(
            ($"Orleans:Streaming:{providerName}:ServiceKey", eventHubServiceKey),
            ($"ConnectionStrings:{eventHubServiceKey}", CreateEventHubsConnectionString("orders", "workers")),
            ("ORDERS_CONSUMER_EVENTHUBNAME", "orders"),
            ("ORDERS_CONSUMER_CONSUMERGROUPNAME", "workers"));

        ConfigureSilo(builder, providerName);

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<OrleansConfigurationException>(
            () => services.GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>().Get(providerName));
        Assert.Contains("CheckpointerServiceKey", exception.Message);
    }

    [Fact]
    public void Assembly_RegistersManualAndAspireProviderTypes()
    {
        var registrations = typeof(EventHubsStreamProviderBuilder)
            .Assembly
            .GetCustomAttributes<RegisterProviderAttribute>()
            .Where(attribute => attribute.Type == typeof(EventHubsStreamProviderBuilder))
            .Select(attribute => (attribute.Name, attribute.Kind, attribute.Target))
            .ToHashSet();

        Assert.Equal(6, registrations.Count);
        Assert.Contains(("EventHubs", "Streaming", "Silo"), registrations);
        Assert.Contains(("EventHubs", "Streaming", "Client"), registrations);
        Assert.Contains(("AzureEventHub", "Streaming", "Silo"), registrations);
        Assert.Contains(("AzureEventHub", "Streaming", "Client"), registrations);
        Assert.Contains(("AzureEventHubConsumerGroup", "Streaming", "Silo"), registrations);
        Assert.Contains(("AzureEventHubConsumerGroup", "Streaming", "Client"), registrations);
    }

    private static void ConfigureSilo(TestSiloBuilder builder, string providerName)
        => new EventHubsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

    private static TestSiloBuilder CreateSiloBuilder(params (string Key, string? Value)[] values)
        => new(CreateConfiguration(values));

    private static TestClientBuilder CreateClientBuilder(params (string Key, string? Value)[] values)
        => new(CreateConfiguration(values));

    private static IConfigurationRoot CreateConfiguration((string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();

    private static string CreateEventHubsConnectionString(string eventHubName, string consumerGroup)
        => $"Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;EntityPath={eventHubName};ConsumerGroup={consumerGroup}";

    private static string CreateEventHubConnectionString(string eventHubName)
        => $"Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;EntityPath={eventHubName}";

    private sealed class TestSiloBuilder(IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = CreateServices(configuration);

        public IConfiguration Configuration { get; } = configuration;
    }

    private sealed class TestClientBuilder(IConfiguration configuration) : IClientBuilder
    {
        public IServiceCollection Services { get; } = CreateServices(configuration);

        public IConfiguration Configuration { get; } = configuration;
    }

    private static ServiceCollection CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IConfiguration>(configuration);
        return services;
    }
}
