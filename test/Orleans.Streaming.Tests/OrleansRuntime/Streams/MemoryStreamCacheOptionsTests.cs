using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace UnitTests.OrleansRuntime.Streams;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
[TestCategory("BVT"), TestCategory("Streaming")]
public sealed class MemoryStreamCacheOptionsTests
{
    [Fact]
    public void DefaultMaxAddCountIs100()
    {
        using var host = CreateHost(builder => builder.AddMemoryStreams("default"));
        var options = host.Services.GetOptionsByName<MemoryStreamCacheOptions>("default");
        IQueueFlowController controller = CreateCache(host.Services, "default");

        Assert.Equal(100, new MemoryStreamCacheOptions().MaxAddCount);
        Assert.Equal(100, options.MaxAddCount);
        Assert.Equal(100, controller.GetMaxAddCount());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(int.MaxValue)]
    public void ConfiguredMaxAddCountFlowsToCache(int maxAddCount)
    {
        using var host = CreateHost(builder => builder.AddMemoryStreams("configured", stream =>
            stream.ConfigureCache(options => options.Configure(value => value.MaxAddCount = maxAddCount))));

        host.Services.GetRequiredService<IStartupValidator>().Validate();
        IQueueFlowController controller = CreateCache(host.Services, "configured");

        Assert.Equal(maxAddCount, controller.GetMaxAddCount());
    }

    [Fact]
    public void NamedProvidersHaveIndependentMaxAddCounts()
    {
        using var host = CreateHost(builder => builder
            .AddMemoryStreams("small", stream =>
                stream.ConfigureCache(options => options.Configure(value => value.MaxAddCount = 25)))
            .AddMemoryStreams("large", stream =>
                stream.ConfigureCache(options => options.Configure(value => value.MaxAddCount = 200)))
            .AddMemoryStreams("default"));

        Assert.Equal(25, CreateCache(host.Services, "small").GetMaxAddCount());
        Assert.Equal(200, CreateCache(host.Services, "large").GetMaxAddCount());
        Assert.Equal(100, CreateCache(host.Services, "default").GetMaxAddCount());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void InvalidMaxAddCountIsRejectedDuringStartupValidation(int maxAddCount)
    {
        var services = new ServiceCollection();
        var configurator = new SiloMemoryStreamConfigurator<DefaultMemoryMessageBodySerializer>(
            "invalid", configure => configure(services));
        configurator.ConfigureCache(options => options.Configure(value => value.MaxAddCount = maxAddCount));
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            serviceProvider.GetRequiredService<IStartupValidator>().Validate);

        Assert.Equal("invalid", exception.OptionsName);
        Assert.Equal(typeof(MemoryStreamCacheOptions), exception.OptionsType);
        Assert.Contains(nameof(MemoryStreamCacheOptions.MaxAddCount), Assert.Single(exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationBasedProviderBindsMaxAddCount()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["memory:MaxAddCount"] = "25",
            })
            .Build();
        using var host = CreateHost(builder =>
            new MemoryStreamProviderBuilder().Configure(builder, "memory", configuration.GetSection("memory")));

        Assert.Equal(25, CreateCache(host.Services, "memory").GetMaxAddCount());
    }

    [Fact]
    public async Task ConfiguredCountBoundsReceiverDequeue()
    {
        using var host = CreateHost(builder => builder.AddMemoryStreams("small", stream =>
            stream.ConfigureCache(options => options.Configure(value => value.MaxAddCount = 25))));
        IQueueFlowController controller = CreateCache(host.Services, "small");
        var serializer = MemoryMessageBodySerializerFactory<DefaultMemoryMessageBodySerializer>.GetOrCreateSerializer(host.Services);
        var queue = new MemoryStreamQueueGrain();
        var streamId = StreamId.Create("namespace", "stream");
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var i = 0; i < 26; i++)
        {
            var payload = serializer.Serialize(new MemoryMessageBody([i], requestContext: null));
            await queue.Enqueue(MemoryMessageData.Create(streamId, payload), cancellationToken);
        }

        IQueueAdapterReceiver receiver = new MemoryAdapterReceiver<DefaultMemoryMessageBodySerializer>(
            queue, NullLogger.Instance, serializer, Substitute.For<IQueueAdapterReceiverMonitor>());

        var first = await receiver.GetQueueMessagesAsync(controller.GetMaxAddCount(), cancellationToken);
        var second = await receiver.GetQueueMessagesAsync(controller.GetMaxAddCount(), cancellationToken);

        Assert.Equal(25, first.Count);
        Assert.Equal(Enumerable.Range(0, 25), first.SelectMany(batch => batch.GetEvents<int>()).Select(item => item.Item1));
        Assert.Equal(25, Assert.Single(Assert.Single(second).GetEvents<int>()).Item1);
        Assert.Empty(await receiver.GetQueueMessagesAsync(controller.GetMaxAddCount(), cancellationToken));
    }

    [Fact]
    public void OriginalCacheConstructorPreservesDefaultMaxAddCount()
    {
        IQueueFlowController controller = new MemoryPooledCache<IMemoryMessageBodySerializer>(
            Substitute.For<IObjectPool<FixedSizeBuffer>>(),
            new TimePurgePredicate(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)),
            NullLogger.Instance,
            serializer: Substitute.For<IMemoryMessageBodySerializer>(),
            cacheMonitor: null,
            monitorWriteInterval: null,
            purgeMetadataInterval: null);

        Assert.Equal(100, controller.GetMaxAddCount());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CacheConstructorRejectsInvalidMaxAddCount(int maxAddCount)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoryPooledCache<IMemoryMessageBodySerializer>(
                Substitute.For<IObjectPool<FixedSizeBuffer>>(),
                new TimePurgePredicate(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)),
                NullLogger.Instance,
                serializer: Substitute.For<IMemoryMessageBodySerializer>(),
                cacheMonitor: null,
                monitorWriteInterval: null,
                purgeMetadataInterval: null,
                maxAddCount));

        Assert.Equal("maxAddCount", exception.ParamName);
    }

    private static IHost CreateHost(Action<ISiloBuilder> configure)
        => new HostBuilder()
            .UseOrleans(builder =>
            {
                builder.UseLocalhostClustering().AddMemoryGrainStorage("PubSubStore");
                configure(builder);
            })
            .Build();

    private static IQueueCache CreateCache(IServiceProvider services, string providerName)
    {
        var factory = services.GetRequiredKeyedService<IQueueAdapterFactory>(providerName);
        var queueId = factory.GetStreamQueueMapper().GetAllQueues().First();
        return factory.GetQueueAdapterCache().CreateQueueCache(queueId);
    }
}
