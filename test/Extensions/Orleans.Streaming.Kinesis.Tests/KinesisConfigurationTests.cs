using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class KinesisConfigurationTests
{
    [Fact]
    public void ClientConfigurationDoesNotRequireCheckpointer()
    {
        const string providerName = "client";
        var services = new ServiceCollection();
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());

        builder.AddKinesisStreams(providerName, options => options.Region = "us-east-1");

        using var serviceProvider = services.BuildServiceProvider();
        Assert.Null(serviceProvider.GetKeyedService<IStreamQueueCheckpointerFactory>(providerName));
        Assert.DoesNotContain(
            serviceProvider.GetServices<IConfigurationValidator>(),
            validator => validator is KinesisStreamCheckpointerConfigurationValidator);
    }

    [Fact]
    public void DefaultSiloConfigurationRegistersGrainCheckpointer()
    {
        const string providerName = "silo";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddKinesisStreams(providerName, options => options.Region = "us-east-1"))
            .Build();

        Assert.IsType<GrainStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(providerName));
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<GrainStreamQueueCheckpointerOptions>>()
            .Get(providerName);
        Assert.Equal(TimeSpan.FromMinutes(1), options.PersistInterval);
        Assert.Equal("PubSubStore", options.StorageProviderName);
        Assert.Same(StreamCheckpointComparers.Numeric, options.CheckpointComparer);
    }
}
