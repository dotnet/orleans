using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streaming.NATS;
using Xunit;

namespace NATS.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("NATS")]
[TestCategory("NATS"), TestCategory("Streaming")]
public sealed class NatsStreamProviderBuilderTests
{
    [Fact]
    public void ConfigureSilo_ServiceKey_UsesKeyedConnectionAndBindsOptions()
    {
        const string providerName = "orders";
        const string serviceKey = "nats";
        var connection = new NatsConnection(NatsTestConstants.NatsClientOptions);
        var builder = new TestSiloBuilder(CreateConfiguration(
            ($"Orleans:Streaming:{providerName}:ServiceKey", serviceKey),
            ($"Orleans:Streaming:{providerName}:StreamName", "orders-stream"),
            ($"Orleans:Streaming:{providerName}:BatchSize", "25"),
            ($"Orleans:Streaming:{providerName}:PartitionCount", "4"),
            ($"Orleans:Streaming:{providerName}:ProducerCount", "2"),
            ($"Orleans:Streaming:{providerName}:StorageType", "Memory")));
        builder.Services.AddKeyedSingleton<INatsConnection>(serviceKey, connection);

        Configure(builder, providerName);

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(providerName);
        var mapperOptions = services.GetRequiredService<IOptionsMonitor<HashRingStreamQueueMapperOptions>>().Get(providerName);

        Assert.Same(connection, options.Connection);
        Assert.Null(options.NatsClientOptions);
        Assert.Equal("orders-stream", options.StreamName);
        Assert.Equal(25, options.BatchSize);
        Assert.Equal(4, options.PartitionCount);
        Assert.Equal(4, mapperOptions.TotalQueueCount);
        Assert.Equal(2, options.ProducerCount);
        Assert.Equal(NATS.Client.JetStream.Models.StreamConfigStorage.Memory, options.StorageType);
    }

    [Fact]
    public void ConfigureClient_ConnectionName_UsesConfiguredConnectionString()
    {
        const string providerName = "telemetry";
        const string connectionName = "nats";
        var builder = new TestClientBuilder(CreateConfiguration(
            ($"Orleans:Streaming:{providerName}:ConnectionName", connectionName),
            ($"Orleans:Streaming:{providerName}:StreamName", "telemetry-stream"),
            ($"ConnectionStrings:{connectionName}", "nats://nats.example:4222")));

        new NatsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(providerName);

        Assert.Null(options.Connection);
        Assert.Equal("nats://nats.example:4222", options.NatsClientOptions!.Url);
    }

    [Fact]
    public void ConfigureClient_MultipleServerUrls_PreservesSeedList()
    {
        const string providerName = "telemetry";
        const string connectionString = "nats://first.example:4222,nats://second.example:4222";
        var builder = new TestClientBuilder(CreateConfiguration(
            ($"Orleans:Streaming:{providerName}:ConnectionString", connectionString),
            ($"Orleans:Streaming:{providerName}:StreamName", "telemetry-stream")));

        new NatsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(providerName);

        Assert.Equal(connectionString, options.NatsClientOptions!.Url);
    }

    [Fact]
    public void LogSafeServerDescription_RedactsCredentials()
    {
        var description = NatsConnectionManager.GetLogSafeServerDescription(
            "nats://user:password@first.example:4222,nats://token@second.example:4222");

        Assert.DoesNotContain("user", description, StringComparison.Ordinal);
        Assert.DoesNotContain("password", description, StringComparison.Ordinal);
        Assert.DoesNotContain("token", description, StringComparison.Ordinal);
        Assert.Contains("first.example", description, StringComparison.Ordinal);
        Assert.Contains("second.example", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ServiceKey", "nats", "ConnectionString", "nats://localhost:4222")]
    [InlineData("ConnectionName", "nats", "Url", "nats://localhost:4222")]
    public void Configure_AmbiguousConnectionConfiguration_Throws(
        string firstKey,
        string firstValue,
        string secondKey,
        string secondValue)
    {
        const string providerName = "orders";
        var builder = new TestSiloBuilder(CreateConfiguration(
            ($"Orleans:Streaming:{providerName}:{firstKey}", firstValue),
            ($"Orleans:Streaming:{providerName}:{secondKey}", secondValue),
            ($"Orleans:Streaming:{providerName}:StreamName", "orders-stream")));

        Configure(builder, providerName);

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<OrleansConfigurationException>(
            () => services.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(providerName));
        Assert.Contains("exactly one", exception.Message);
    }

    [Fact]
    public void Configure_MissingConnectionConfiguration_Throws()
    {
        const string providerName = "orders";
        var builder = new TestSiloBuilder(CreateConfiguration(
            ($"Orleans:Streaming:{providerName}:StreamName", "orders-stream")));

        Configure(builder, providerName);

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<OrleansConfigurationException>(
            () => services.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(providerName));
        Assert.Contains("ServiceKey", exception.Message);
    }

    [Fact]
    public void Configure_MissingKeyedConnection_Throws()
    {
        const string providerName = "orders";
        const string serviceKey = "nats";
        var builder = new TestSiloBuilder(CreateConfiguration(
            ($"Orleans:Streaming:{providerName}:ServiceKey", serviceKey),
            ($"Orleans:Streaming:{providerName}:StreamName", "orders-stream")));

        Configure(builder, providerName);

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<OrleansConfigurationException>(
            () => services.GetRequiredService<IOptionsMonitor<NatsOptions>>().Get(providerName));
        Assert.Contains("AddKeyedNatsClient", exception.Message);
    }

    [Fact]
    public void Assembly_RegistersStableManualAndAspireProviderTypes()
    {
        var registrations = typeof(NatsStreamProviderBuilder)
            .Assembly
            .GetCustomAttributes<RegisterProviderAttribute>()
            .Where(attribute => attribute.Type == typeof(NatsStreamProviderBuilder))
            .Select(attribute => (attribute.Name, attribute.Kind, attribute.Target))
            .ToHashSet();

        Assert.Equal(6, registrations.Count);
        Assert.Contains(("NATS", "Streaming", "Silo"), registrations);
        Assert.Contains(("NATS", "Streaming", "Client"), registrations);
        Assert.Contains(("Nats", "Streaming", "Silo"), registrations);
        Assert.Contains(("Nats", "Streaming", "Client"), registrations);
        Assert.Contains(("NatsServer", "Streaming", "Silo"), registrations);
        Assert.Contains(("NatsServer", "Streaming", "Client"), registrations);
    }

    private static void Configure(TestSiloBuilder builder, string providerName)
        => new NatsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

    private static IConfigurationRoot CreateConfiguration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();

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
