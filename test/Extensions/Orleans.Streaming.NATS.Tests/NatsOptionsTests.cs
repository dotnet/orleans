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

        var streamName = $"test-replicas-{Guid.NewGuid()}";
        await using var natsConnection = new NatsConnection();
        var natsContext = new NatsJSContext(natsConnection);

        await natsConnection.ConnectAsync();

        try
        {
            var streamConfig = new StreamConfig(streamName, [$"test-replicas-provider.>"])
            {
                Retention = StreamConfigRetention.Workqueue,
                NumReplicas = 1
            };

            var stream = await natsContext.CreateStreamAsync(streamConfig);
            var info = stream.Info;

            Assert.Equal(1, info.Config.NumReplicas);
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
}
