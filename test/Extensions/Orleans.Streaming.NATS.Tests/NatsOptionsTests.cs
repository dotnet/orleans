using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Orleans.Runtime;
using Orleans.Streaming.NATS;
using TestExtensions;
using Xunit;

namespace NATS.Tests;

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

    [SkippableFact]
    public async Task NumReplicas_IsAppliedToJetStreamConfig()
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw new SkipException("Nats Server is not available");
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
        await connectionManager.Initialize();

        await using var natsConnection = new NatsConnection();
        var natsContext = new NatsJSContext(natsConnection);
        await natsConnection.ConnectAsync();

        try
        {
            var stream = await natsContext.GetStreamAsync(streamName);
            var info = stream.Info;

            Assert.Equal(1, info.Config.NumReplicas);
            Assert.Equal(StreamConfigRetention.Workqueue, info.Config.Retention);
        }
        finally
        {
            try
            {
                var stream = await natsContext.GetStreamAsync(streamName);
                await stream.DeleteAsync();
            }
            catch (NatsJSApiException)
            {
                // Ignore cleanup errors
            }
        }
    }

    // NOTE: Testing NumReplicas > 1 (e.g. R3) requires a multi-node NATS JetStream
    // cluster. A single NATS node only supports NumReplicas = 1. R3 integration
    // testing should be done in a CI environment with a 3-node cluster configured
    // via docker-compose or similar infrastructure.

    [SkippableTheory]
    [InlineData(StreamConfigStorage.File)]
    [InlineData(StreamConfigStorage.Memory)]
    public async Task StorageType_IsAppliedToJetStreamConfig(StreamConfigStorage storageType)
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw new SkipException("Nats Server is not available");
        }

        var providerName = $"test-storage-{Guid.NewGuid():N}";
        var streamName = $"test-storage-stream-{Guid.NewGuid():N}";
        var options = new NatsOptions
        {
            StreamName = streamName,
            NumReplicas = 1,
            PartitionCount = 2,
            ProducerCount = 1,
            StorageType = storageType
        };

        var connectionManager = new NatsConnectionManager(providerName, NullLoggerFactory.Instance, options);
        await connectionManager.Initialize();

        await using var natsConnection = new NatsConnection();
        var natsContext = new NatsJSContext(natsConnection);
        await natsConnection.ConnectAsync();

        try
        {
            var stream = await natsContext.GetStreamAsync(streamName);
            var info = stream.Info;

            Assert.Equal(storageType, info.Config.Storage);
        }
        finally
        {
            try
            {
                var stream = await natsContext.GetStreamAsync(streamName);
                await stream.DeleteAsync();
            }
            catch (NatsJSApiException)
            {
                // Ignore cleanup errors
            }
        }
    }
}
