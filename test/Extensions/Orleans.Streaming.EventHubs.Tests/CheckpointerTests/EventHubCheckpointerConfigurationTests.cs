using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests.CheckpointerTests;

[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class EventHubCheckpointerConfigurationTests
{
    [Fact]
    public void GenericUseGrainCheckpointer_ExplicitlySelected_RegistersGrainFactory()
    {
        const string providerName = "grain-checkpointer";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddMemoryGrainStorage(ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME)
                .AddEventHubStreams(providerName, stream =>
                {
                    stream.ConfigureEventHub(options => options.Configure(_ => { }));
                    stream.UseGrainCheckpointer();
                }))
            .Build();

        Assert.IsType<GrainStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(providerName));
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<GrainStreamQueueCheckpointerOptions>>()
            .Get(providerName);
        Assert.Equal(TimeSpan.FromMinutes(1), options.PersistInterval);
        Assert.Same(StreamCheckpointComparers.Numeric, options.CheckpointComparer);
    }

    [Fact]
    public void ConvenienceOverload_RegistersAzureTableFactoryByDefault()
    {
        const string providerName = "azure-table-checkpointer";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddEventHubStreams(
                    providerName,
                    configureEventHub: _ => { },
                    configureDefaultCheckpointer: _ => { }))
            .Build();

        Assert.IsType<AzureTableStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(providerName));
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>()
            .Get(providerName);
        Assert.Same(StreamCheckpointComparers.Numeric, options.CheckpointComparer);
    }

    [Fact]
    public void GenericUseAzureTableCheckpointer_CanConfigureAnotherStreamProvider()
    {
        const string providerName = "memory-with-azure-table-checkpointer";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddMemoryStreams(providerName, stream =>
                    stream.UseAzureTableCheckpointer(options => options.Configure(value =>
                    {
                        value.TableServiceClient = new("UseDevelopmentStorage=true");
                        value.CheckpointComparer = StringComparer.Ordinal;
                    }))))
            .Build();

        Assert.IsType<AzureTableStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(providerName));
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>()
            .Get(providerName);
        Assert.Same(StringComparer.Ordinal, options.CheckpointComparer);
    }

    [Fact]
    public void GenericUseAzureTableCheckpointer_CustomComparerOverridesEventHubDefault()
    {
        const string providerName = "event-hubs-with-custom-table-checkpointer";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddEventHubStreams(providerName, stream =>
                {
                    stream.ConfigureEventHub(options => options.Configure(_ => { }));
                    stream.UseAzureTableCheckpointer(options => options.Configure(value =>
                    {
                        value.TableServiceClient = new("UseDevelopmentStorage=true");
                        value.CheckpointComparer = StringComparer.Ordinal;
                    }));
                }))
            .Build();

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<AzureTableStreamCheckpointerOptions>>()
            .Get(providerName);
        Assert.Same(StringComparer.Ordinal, options.CheckpointComparer);
    }

    [Fact]
    public void GenericUseGrainCheckpointer_CustomOptions_OverrideEventHubDefaults()
    {
        const string providerName = "custom-grain-checkpointer";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddMemoryGrainStorage("CheckpointStore")
                .AddEventHubStreams(providerName, stream =>
                {
                    stream.ConfigureEventHub(options => options.Configure(_ => { }));
                    stream.UseGrainCheckpointer(options => options.Configure(value =>
                    {
                        value.PersistInterval = TimeSpan.FromSeconds(7);
                        value.StorageProviderName = "CheckpointStore";
                        value.CheckpointComparer = StringComparer.Ordinal;
                    }));
                }))
            .Build();

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<GrainStreamQueueCheckpointerOptions>>()
            .Get(providerName);
        Assert.Equal(TimeSpan.FromSeconds(7), options.PersistInterval);
        Assert.Equal("CheckpointStore", options.StorageProviderName);
        Assert.Same(StringComparer.Ordinal, options.CheckpointComparer);
    }

    [Fact]
    public void GenericUseGrainCheckpointer_MissingStorageProvider_FailsValidation()
    {
        const string providerName = "missing-storage";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddMemoryStreams(providerName, stream =>
                    stream.UseGrainCheckpointer(options => options.Configure(
                        value => value.StorageProviderName = "MissingStore"))))
            .Build();

        var exception = Assert.Throws<OptionsValidationException>(
            () => host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(providerName));

        Assert.Contains(
            nameof(GrainStreamQueueCheckpointerOptions.StorageProviderName),
            exception.Message);
    }

    [Fact]
    public void GenericUseGrainCheckpointer_DifferentProvidersCanUseDifferentStorage()
    {
        const string azureStream = "azure-stream";
        const string cosmosStream = "cosmos-stream";
        const string azureStorage = "AzureCheckpointStore";
        const string cosmosStorage = "CosmosCheckpointStore";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddMemoryGrainStorage(azureStorage)
                .AddMemoryGrainStorage(cosmosStorage)
                .AddMemoryStreams(azureStream, stream =>
                    stream.UseGrainCheckpointer(options => options.Configure(
                        value => value.StorageProviderName = azureStorage)))
                .AddMemoryStreams(cosmosStream, stream =>
                    stream.UseGrainCheckpointer(options => options.Configure(
                        value => value.StorageProviderName = cosmosStorage))))
            .Build();

        _ = host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(azureStream);
        _ = host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(cosmosStream);
        var options = host.Services.GetRequiredService<IOptionsMonitor<GrainStreamQueueCheckpointerOptions>>();

        Assert.Equal(azureStorage, options.Get(azureStream).StorageProviderName);
        Assert.Equal(cosmosStorage, options.Get(cosmosStream).StorageProviderName);
    }
}
