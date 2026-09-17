using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streaming.NATS;
using Orleans.Streaming.NATS.Hosting;
using TestExtensions;
using Xunit;

namespace NATS.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("NATS")]
[TestCategory("NATS")]
public sealed class NatsOptionsTests
{
    [Fact]
    public void DefaultNumReplicas_ShouldBeOne()
    {
        var options = new NatsOptions();

        Assert.Equal(1, options.NumReplicas);
    }

    [Fact]
    public void DefaultStorageType_ShouldBeFile()
    {
        var options = new NatsOptions();

        Assert.Equal(StreamConfigStorage.File, options.StorageType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validator_InvalidNumReplicas_ShouldThrow(int numReplicas)
    {
        var options = new NatsOptions
        {
            StreamName = "test-stream",
            NumReplicas = numReplicas
        };

        var validator = new NatsStreamOptionsValidator(options, "test-provider");

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Validator_ValidNumReplicas_ShouldNotThrow(int numReplicas)
    {
        var options = new NatsOptions
        {
            StreamName = "test-stream",
            NumReplicas = numReplicas
        };

        var validator = new NatsStreamOptionsValidator(options, "test-provider");

        validator.ValidateConfiguration();
    }

    [Theory]
    [InlineData(false, 0, 8, 8, nameof(NatsOptions.BatchSize))]
    [InlineData(false, -1, 8, 8, nameof(NatsOptions.BatchSize))]
    [InlineData(false, 100, 0, 8, nameof(NatsOptions.PartitionCount))]
    [InlineData(false, 100, -1, 8, nameof(NatsOptions.PartitionCount))]
    [InlineData(false, 100, 8, 0, nameof(NatsOptions.ProducerCount))]
    [InlineData(false, 100, 8, -1, nameof(NatsOptions.ProducerCount))]
    [InlineData(true, 0, 8, 8, nameof(NatsOptions.BatchSize))]
    [InlineData(true, -1, 8, 8, nameof(NatsOptions.BatchSize))]
    [InlineData(true, 100, 0, 8, nameof(NatsOptions.PartitionCount))]
    [InlineData(true, 100, -1, 8, nameof(NatsOptions.PartitionCount))]
    [InlineData(true, 100, 8, 0, nameof(NatsOptions.ProducerCount))]
    [InlineData(true, 100, 8, -1, nameof(NatsOptions.ProducerCount))]
    public void Validator_InvalidDimensions_ShouldThrow(
        bool useClient, int batchSize, int partitionCount, int producerCount, string optionName)
    {
        const string providerName = "test-provider";
        using var host = CreateHost(useClient, providerName, options =>
        {
            options.StreamName = "test-stream";
            options.BatchSize = batchSize;
            options.PartitionCount = partitionCount;
            options.ProducerCount = producerCount;
        });
        var validator = Assert.Single(
            host.Services.GetServices<IConfigurationValidator>(),
            value => value is NatsStreamOptionsValidator);

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Equal(
            $"The {optionName} must be at least 1 for the NATS stream provider '{providerName}'.",
            exception.Message);
    }

    [Theory]
    [InlineData(false, 1, 1, 1)]
    [InlineData(false, 100, 8, 8)]
    [InlineData(false, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(true, 1, 1, 1)]
    [InlineData(true, 100, 8, 8)]
    [InlineData(true, int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Validator_ValidDimensions_ShouldNotThrow(
        bool useClient, int batchSize, int partitionCount, int producerCount)
    {
        using var host = CreateHost(useClient, "test-provider", options =>
        {
            options.StreamName = "test-stream";
            options.BatchSize = batchSize;
            options.PartitionCount = partitionCount;
            options.ProducerCount = producerCount;
        });
        var validator = Assert.Single(
            host.Services.GetServices<IConfigurationValidator>(),
            value => value is NatsStreamOptionsValidator);

        validator.ValidateConfiguration();
    }

    private static IHost CreateHost(bool useClient, string providerName, Action<NatsOptions> configureOptions)
    {
        var builder = new HostBuilder();
        if (useClient)
        {
            builder.UseOrleansClient(client => client
                .UseLocalhostClustering()
                .AddNatsStreams(providerName, configureOptions));
        }
        else
        {
            builder.UseOrleans(silo => silo
                .UseLocalhostClustering()
                .AddNatsStreams(providerName, configureOptions));
        }

        return builder.Build();
    }

    [Fact]
    public void Validator_MissingStreamName_ShouldThrow()
    {
        var options = new NatsOptions
        {
            StreamName = null!,
            NumReplicas = 1
        };

        var validator = new NatsStreamOptionsValidator(options, "test-provider");

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Fact]
    public void Validator_EmptyStreamName_ShouldThrow()
    {
        var options = new NatsOptions
        {
            StreamName = "  ",
            NumReplicas = 1
        };

        var validator = new NatsStreamOptionsValidator(options, "test-provider");

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Fact]
    public async Task NumReplicas_IsAppliedToJetStreamConfig()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Nats Server is not available");
        }

        var providerName = $"test-replicas-{Guid.NewGuid():N}";
        var streamName = $"test-replicas-stream-{Guid.NewGuid():N}";
        var options = new NatsOptions
        {
            StreamName = streamName,
            NumReplicas = 1,
            PartitionCount = 2,
            ProducerCount = 1
        };

        var connectionManager = new NatsConnectionManager(providerName, NullLoggerFactory.Instance, options);
        await connectionManager.Initialize(cancellationToken);

        await using var natsConnection = new NatsConnection();
        var natsContext = new NatsJSContext(natsConnection);
        await natsConnection.ConnectAsync();

        try
        {
            var stream = await natsContext.GetStreamAsync(streamName, cancellationToken: cancellationToken);
            var info = stream.Info;

            Assert.Equal(1, info.Config.NumReplicas);
            Assert.Equal(StreamConfigRetention.Workqueue, info.Config.Retention);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var stream = await natsContext.GetStreamAsync(streamName, cancellationToken: cleanup.Token);
                await stream.DeleteAsync(cleanup.Token);
            }
            catch (NatsJSApiException)
            {
                // Ignore cleanup errors
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }
        }
    }

    // NOTE: Testing NumReplicas > 1 (e.g. R3) requires a multi-node NATS JetStream
    // cluster. A single NATS node only supports NumReplicas = 1. R3 integration
    // testing should be done in a CI environment with a 3-node cluster configured
    // via docker-compose or similar infrastructure.

    [Theory]
    [InlineData(StreamConfigStorage.File)]
    [InlineData(StreamConfigStorage.Memory)]
    public async Task StorageType_IsAppliedToJetStreamConfig(StreamConfigStorage storageType)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Nats Server is not available");
        }

        var providerName = $"test-storage-{Guid.NewGuid():N}";
        var streamName = $"test-storage-stream-{Guid.NewGuid():N}";
        var options = new NatsOptions
        {
            StreamName = streamName,
            NumReplicas = 1,
            PartitionCount = 2,
            ProducerCount = 1,
            StorageType = storageType,
            NatsClientOptions = NatsTestConstants.NatsClientOptions
        };

        var connectionManager = new NatsConnectionManager(providerName, NullLoggerFactory.Instance, options);
        await connectionManager.Initialize(cancellationToken);

        await using var natsConnection = new NatsConnection(NatsTestConstants.NatsClientOptions);
        var natsContext = new NatsJSContext(natsConnection);
        await natsConnection.ConnectAsync();

        try
        {
            var stream = await natsContext.GetStreamAsync(streamName, cancellationToken: cancellationToken);
            var info = stream.Info;

            Assert.Equal(storageType, info.Config.Storage);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var stream = await natsContext.GetStreamAsync(streamName, cancellationToken: cleanup.Token);
                await stream.DeleteAsync(cleanup.Token);
            }
            catch (NatsJSApiException)
            {
                // Ignore cleanup errors
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }
        }
    }
}
