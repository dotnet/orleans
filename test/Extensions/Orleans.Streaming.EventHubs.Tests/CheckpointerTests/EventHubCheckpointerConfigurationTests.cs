using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests.CheckpointerTests;

[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class EventHubCheckpointerConfigurationTests
{
    [Fact]
    public void UseGrainCheckpointer_ExplicitlySelected_RegistersGrainFactory()
    {
        const string providerName = "grain-checkpointer";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
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

        Assert.IsType<EventHubCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(providerName));
    }

    [Fact]
    public void UseGrainCheckpointer_CustomOptions_OverrideEventHubDefaults()
    {
        const string providerName = "custom-grain-checkpointer";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
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
}
