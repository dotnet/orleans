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

    [Fact]
    public void RabbitMQClientOptions_DefaultAndExplicitMaxLengthBytesArePreserved()
    {
        var defaultOptions = new RabbitMQClientOptions();
        const ulong explicitMaxLengthBytes = 5UL * 1024 * 1024 * 1024 + 17;
        var explicitOptions = new RabbitMQClientOptions
        {
            StreamOptions = new StreamSpec("explicit-retention")
            {
                MaxLengthBytes = explicitMaxLengthBytes
            }
        };

        Assert.Equal(
            RabbitMQClientOptions.DEFAULT_STREAM_MAX_LENGTH_BYTES.ToString(System.Globalization.CultureInfo.InvariantCulture),
            defaultOptions.StreamOptions.Args["max-length-bytes"]);
        Assert.Equal("209715200", defaultOptions.StreamOptions.Args["max-length-bytes"]);
        Assert.Equal(
            explicitMaxLengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            explicitOptions.StreamOptions.Args["max-length-bytes"]);
    }

    [Fact]
    public async Task AddRabbitMQStreams_ClientRegistrationResolvesIsolatedNamedServicesAndLifecycleParticipants()
    {
        const string providerA = "rabbit-a";
        const string providerB = "rabbit-b";
        const ulong providerAMaxLengthBytes = 201UL * 1024 * 1024;
        const ulong providerBMaxLengthBytes = 202UL * 1024 * 1024;
        var host = Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient(
            new Microsoft.Extensions.Hosting.HostBuilder(),
            (_, builder) =>
            {
                Orleans.Hosting.ClientBuilderExtensions.UseLocalhostClustering(builder);
                builder.AddRabbitMQStreams(providerA, (RabbitMQClientOptions options) =>
                    options.StreamOptions = new StreamSpec("client-a")
                    {
                        MaxLengthBytes = providerAMaxLengthBytes
                    });
                builder.AddRabbitMQStreams(providerB, (RabbitMQClientOptions options) =>
                    options.StreamOptions = new StreamSpec("client-b")
                    {
                        MaxLengthBytes = providerBMaxLengthBytes
                    });
            }).Build();
        await using var hostDisposal = (IAsyncDisposable)host;
        var services = host.Services;

        var streamProviderA = services.GetRequiredKeyedService<IStreamProvider>(providerA);
        var streamProviderB = services.GetRequiredKeyedService<IStreamProvider>(providerB);
        var factoryA = services.GetRequiredKeyedService<IQueueAdapterFactory>(providerA);
        var factoryB = services.GetRequiredKeyedService<IQueueAdapterFactory>(providerB);
        var systemA = services.GetRequiredKeyedService<RabbitMQStreamSystemProvider>(providerA);
        var systemB = services.GetRequiredKeyedService<RabbitMQStreamSystemProvider>(providerB);
        var queueProviderA = services.GetRequiredKeyedService<RabbitMQQueueProvider>(providerA);
        var queueProviderB = services.GetRequiredKeyedService<RabbitMQQueueProvider>(providerB);
        var receiverFactoryA = services.GetRequiredKeyedService<Orleans.Streaming.RabbitMQ.Adapters.RabbitMQAdapterReceiverFactory>(providerA);
        var receiverFactoryB = services.GetRequiredKeyedService<Orleans.Streaming.RabbitMQ.Adapters.RabbitMQAdapterReceiverFactory>(providerB);
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RabbitMQClientOptions>>();
        var optionsA = options.Get(providerA);
        var optionsB = options.Get(providerB);
        var lifecycleParticipants = services.GetServices<ILifecycleParticipant<IClusterClientLifecycle>>().ToArray();

        Assert.IsType<Orleans.Providers.Streams.Common.PersistentStreamProvider>(streamProviderA);
        Assert.IsType<Orleans.Providers.Streams.Common.PersistentStreamProvider>(streamProviderB);
        Assert.NotSame(streamProviderA, streamProviderB);
        Assert.IsType<Orleans.Streaming.RabbitMQ.Adapters.RabbitMQAdapterFactory>(factoryA);
        Assert.IsType<Orleans.Streaming.RabbitMQ.Adapters.RabbitMQAdapterFactory>(factoryB);
        Assert.NotSame(factoryA, factoryB);
        Assert.NotSame(systemA, systemB);
        Assert.NotSame(queueProviderA, queueProviderB);
        Assert.NotSame(receiverFactoryA, receiverFactoryB);
        Assert.NotSame(optionsA, optionsB);
        Assert.Equal(
            providerAMaxLengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            optionsA.StreamOptions.Args["max-length-bytes"]);
        Assert.Equal(
            providerBMaxLengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            optionsB.StreamOptions.Args["max-length-bytes"]);
        Assert.Same(
            factoryA,
            Assert.Single(lifecycleParticipants, participant => ReferenceEquals(participant, factoryA)));
        Assert.Same(
            factoryB,
            Assert.Single(lifecycleParticipants, participant => ReferenceEquals(participant, factoryB)));
    }

    [Fact]
    public async Task AddRabbitMQStreams_PublicClientAndSiloBuildersHaveNamedRegistrationParity()
    {
        const string providerName = "rabbit-parity";
        const ulong maxLengthBytes = 203UL * 1024 * 1024;
        var clientHost = Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient(
            new Microsoft.Extensions.Hosting.HostBuilder(),
            (_, builder) =>
            {
                Orleans.Hosting.ClientBuilderExtensions.UseLocalhostClustering(builder);
                builder.AddRabbitMQStreams(providerName, (RabbitMQClientOptions options) =>
                    options.StreamOptions = new StreamSpec("client-parity")
                    {
                        MaxLengthBytes = maxLengthBytes
                    });
            }).Build();
        await using var clientHostDisposal = (IAsyncDisposable)clientHost;
        var siloHost = Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans(
            new Microsoft.Extensions.Hosting.HostBuilder(),
            (_, builder) =>
            {
                Orleans.Hosting.CoreHostingExtensions.UseLocalhostClustering(builder);
                builder.AddRabbitMQStreams(providerName, (RabbitMQClientOptions options) =>
                    options.StreamOptions = new StreamSpec("silo-parity")
                    {
                        MaxLengthBytes = maxLengthBytes
                    });
            }).Build();
        await using var siloHostDisposal = (IAsyncDisposable)siloHost;
        var clientServices = clientHost.Services;
        var siloServices = siloHost.Services;

        var clientStreamProvider = clientServices.GetRequiredKeyedService<IStreamProvider>(providerName);
        var siloStreamProvider = siloServices.GetRequiredKeyedService<IStreamProvider>(providerName);
        var clientFactory = clientServices.GetRequiredKeyedService<IQueueAdapterFactory>(providerName);
        var siloFactory = siloServices.GetRequiredKeyedService<IQueueAdapterFactory>(providerName);
        var clientSystem = clientServices.GetRequiredKeyedService<RabbitMQStreamSystemProvider>(providerName);
        var siloSystem = siloServices.GetRequiredKeyedService<RabbitMQStreamSystemProvider>(providerName);
        var clientQueueProvider = clientServices.GetRequiredKeyedService<RabbitMQQueueProvider>(providerName);
        var siloQueueProvider = siloServices.GetRequiredKeyedService<RabbitMQQueueProvider>(providerName);
        var clientReceiverFactory = clientServices.GetRequiredKeyedService<Orleans.Streaming.RabbitMQ.Adapters.RabbitMQAdapterReceiverFactory>(providerName);
        var siloReceiverFactory = siloServices.GetRequiredKeyedService<Orleans.Streaming.RabbitMQ.Adapters.RabbitMQAdapterReceiverFactory>(providerName);
        var clientOptions = clientServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RabbitMQClientOptions>>()
            .Get(providerName);
        var siloOptions = siloServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RabbitMQClientOptions>>()
            .Get(providerName);
        var clientLifecycleParticipants = clientServices
            .GetServices<ILifecycleParticipant<IClusterClientLifecycle>>()
            .ToArray();
        var siloLifecycleParticipants = siloServices
            .GetServices<ILifecycleParticipant<ISiloLifecycle>>()
            .ToArray();

        Assert.IsType<Orleans.Providers.Streams.Common.PersistentStreamProvider>(clientStreamProvider);
        Assert.IsType<Orleans.Providers.Streams.Common.PersistentStreamProvider>(siloStreamProvider);
        Assert.Equal(clientStreamProvider.GetType(), siloStreamProvider.GetType());
        Assert.NotSame(clientStreamProvider, siloStreamProvider);
        Assert.Equal(clientFactory.GetType(), siloFactory.GetType());
        Assert.Equal(clientSystem.GetType(), siloSystem.GetType());
        Assert.Equal(clientQueueProvider.GetType(), siloQueueProvider.GetType());
        Assert.Equal(clientReceiverFactory.GetType(), siloReceiverFactory.GetType());
        Assert.Equal(
            maxLengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            clientOptions.StreamOptions.Args["max-length-bytes"]);
        Assert.Equal(
            maxLengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            siloOptions.StreamOptions.Args["max-length-bytes"]);
        Assert.Same(
            clientFactory,
            Assert.Single(clientLifecycleParticipants, participant => ReferenceEquals(participant, clientFactory)));
        Assert.Same(
            siloFactory,
            Assert.Single(siloLifecycleParticipants, participant => ReferenceEquals(participant, siloFactory)));
    }

    [Fact]
    public async Task AdapterFactoryDisposalClosesStreamSystemProvider()
    {
        var streamSystemProvider = new RabbitMQStreamSystemProvider(
            new RabbitMQClientOptions(),
            NullLogger<RabbitMQStreamSystemProvider>.Instance);
        var factory = new Orleans.Streaming.RabbitMQ.Adapters.RabbitMQAdapterFactory(
            NullLoggerFactory.Instance,
            "provider",
            new RabbitMQQueueCacheOptions(),
            new RabbitMQClientOptions(),
            null!,
            null!,
            streamSystemProvider,
            null!,
            new HashRingStreamQueueMapperOptions());

        await factory.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await streamSystemProvider.GetProducerStream());
    }
}
