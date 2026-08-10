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
                .AddMemoryGrainStorage("PubSubStore")
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

    [Fact]
    public void DynamoDBCheckpointerConfigurationRegistersFactoryAndOptions()
    {
        const string providerName = "dynamodb";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddKinesisStreams(providerName, stream =>
                {
                    stream.ConfigureKinesis(options => options.Region = "us-east-1");
                    stream.UseDynamoDBCheckpointer(options =>
                    {
                        options.Service = "us-west-2";
                        options.TableName = "checkpoints";
                        options.PersistInterval = TimeSpan.FromSeconds(5);
                    });
                }))
            .Build();

        Assert.IsType<DynamoDBStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(providerName));
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
            .Get(providerName);
        Assert.Equal("us-west-2", options.Service);
        Assert.Equal("checkpoints", options.TableName);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PersistInterval);
        Assert.False(options.UseProvisionedThroughput);
    }

    [Fact]
    public void DynamoDBCheckpointerConfigurationRejectsIncompleteCredentials()
    {
        const string providerName = "invalid-dynamodb";
        using var host = new HostBuilder()
            .UseOrleans(builder => builder
                .UseLocalhostClustering()
                .AddKinesisStreams(providerName, stream =>
                {
                    stream.ConfigureKinesis(options => options.Region = "us-east-1");
                    stream.UseDynamoDBCheckpointer(options => options.AccessKey = "access-key");
                }))
            .Build();
        var validator = Assert.Single(
            host.Services.GetServices<IConfigurationValidator>(),
            value => value is DynamoDBStreamQueueCheckpointerOptionsValidator);

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(DynamoDBStreamQueueCheckpointerOptions.SecretKey), exception.Message, StringComparison.Ordinal);
    }
}
