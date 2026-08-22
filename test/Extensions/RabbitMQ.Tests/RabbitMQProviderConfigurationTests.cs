using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Streaming.RabbitMQ.Configurators;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;
using RabbitMQ.Stream.Client;
using Xunit;

namespace RabbitMQ.Tests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Streaming")]
public class RabbitMQProviderConfigurationTests
{
    [Fact]
    public async Task ExplicitQueueNamesAreUsedAsBrokerStreamNames()
    {
        await using var services = CreateServices(
            ("provider", new RabbitMQClientOptions { QueueNames = ["orders", "payments"] }));
        var queueProvider = services.GetRequiredKeyedService<RabbitMQQueueProvider>("provider");
        var mapper = new HashRingBasedPartitionedStreamQueueMapper(["orders", "payments"], "provider");

        var actual = mapper.GetAllQueues().Select(queueProvider.GetQueueName);

        Assert.Equal(["orders", "payments"], actual);
    }

    [Fact]
    public async Task NamedProvidersResolveIsolatedRuntimeDependenciesAndOptions()
    {
        await using var services = CreateServices(
            ("first", new RabbitMQClientOptions { QueueNames = ["first-queue"] }),
            ("second", new RabbitMQClientOptions { QueueNames = ["second-queue"] }));
        var firstSystem = services.GetRequiredKeyedService<RabbitMQStreamSystemProvider>("first");
        var secondSystem = services.GetRequiredKeyedService<RabbitMQStreamSystemProvider>("second");
        var firstQueueProvider = services.GetRequiredKeyedService<RabbitMQQueueProvider>("first");
        var secondQueueProvider = services.GetRequiredKeyedService<RabbitMQQueueProvider>("second");

        Assert.NotSame(firstSystem, secondSystem);
        Assert.NotSame(firstQueueProvider, secondQueueProvider);
        Assert.Equal(
            "first-queue",
            firstQueueProvider.GetQueueName(new HashRingBasedPartitionedStreamQueueMapper(["first-queue"], "first").GetAllQueues().Single()));
        Assert.Equal(
            "second-queue",
            secondQueueProvider.GetQueueName(new HashRingBasedPartitionedStreamQueueMapper(["second-queue"], "second").GetAllQueues().Single()));
    }

    [Fact]
    public void OptionsFormatterDoesNotIncludeCredentials()
    {
        var options = new RabbitMQClientOptions
        {
            StreamSystemConfig = new StreamSystemConfig
            {
                UserName = "sensitive-user",
                Password = "sensitive-password"
            }
        };

        var formatted = string.Join(Environment.NewLine, new RabbitMQClientOptionsFormatter("provider", options).Format());

        Assert.DoesNotContain("sensitive-user", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-password", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposedProducerRejectsNewMessagesWithoutConnecting()
    {
        var producer = new RabbitMQProducer(null!, null!, default, NullLoggerFactory.Instance);

        await producer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => producer.SendMessage([]));
    }

    [Fact]
    public void BatchedContainerDeliveryIsRejected()
    {
        var validator = new RabbitMQStreamOptionsValidator(
            new StreamPullingAgentOptions { BatchContainerBatchSize = 2 },
            "provider");

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(nameof(StreamPullingAgentOptions.BatchContainerBatchSize), exception.Message);
        Assert.Contains("provider", exception.Message);
    }

    private static ServiceProvider CreateServices(params (string Name, RabbitMQClientOptions Options)[] providers)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        foreach (var (name, options) in providers)
        {
            var configurator = new RabbitMQSiloConfigurator(name, configure => configure(services));
            configurator.ConfigureRabbitMQ(builder => builder.Configure(configured =>
            {
                configured.QueueNames = options.QueueNames;
                configured.StreamSystemConfig = options.StreamSystemConfig;
            }));
        }

        return services.BuildServiceProvider();
    }
}
