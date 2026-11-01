using System.Reflection;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Statistics;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class EventHubActivationTests
{
    private const string ProviderName = "activation";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public async Task InitializationFailure_UsesProviderCleanup(
        bool genericRegistration, bool buffered, bool ownsConnection, bool failConstruction)
    {
        var expected = new InvalidOperationException("activation failed");
        await using var connection = new TrackingConnection();
        using var host = CreateHost(connection, genericRegistration, buffered, ownsConnection,
            failConstruction ? null : expected, failConstruction);
        var factory = Assert.IsType<EventHubAdapterFactory>(
            host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(ProviderName));
        Assert.Null(factory.EventHubReceivers);
        var provider = Assert.IsType<PersistentStreamProvider>(
            host.Services.GetRequiredKeyedService<IStreamProvider>(ProviderName));

        var failure = await Record.ExceptionAsync(
            () => Initialize(provider, TestContext.Current.CancellationToken)
                .WaitAsync(Timeout, TestContext.Current.CancellationToken));
        if (failConstruction)
        {
            Assert.Equal("ConnectionOptions", Assert.IsType<ArgumentNullException>(failure).ParamName);
        }
        else
        {
            Assert.Same(expected, failure);
        }

        Assert.Equal(ownsConnection ? 1 : 0, connection.CloseCount);
        Assert.Equal(ownsConnection, connection.IsClosed);
        if (!failConstruction)
        {
            var producer = Assert.IsAssignableFrom<IEventHubProducer>(typeof(EventHubAdapterFactory)
                .GetField("producer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(factory));
            if (buffered)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(
                    () => producer.SendAsync(new EventData(new byte[] { 1 }), "partition"));
            }
            else
            {
                var closed = await Assert.ThrowsAsync<EventHubsException>(
                    () => producer.SendAsync(new EventData(new byte[] { 1 }), "partition"));
                Assert.Equal(EventHubsException.FailureReason.ClientClosed, closed.Reason);
            }
        }

        await factory.ShutdownAsync(CancellationToken.None);
        Assert.Equal(ownsConnection ? 1 : 0, connection.CloseCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.CreateAdapter());
    }

    [Fact]
    public async Task InitializationFailure_CancellationBoundsTheCleanupWait()
    {
        var closeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new TrackingConnection();
        using var host = CreateHost(connection, false, false, true, null, true);
        var factory = Assert.IsType<EventHubAdapterFactory>(
            host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(ProviderName));
        var provider = Assert.IsType<PersistentStreamProvider>(
            host.Services.GetRequiredKeyedService<IStreamProvider>(ProviderName));
        using var cancellation = new CancellationTokenSource();

        try
        {
            connection.CloseRelease = closeRelease.Task;
            var initialization = Initialize(provider, cancellation.Token);
            await connection.CloseCalled.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.False(initialization.IsCompleted);
            await cancellation.CancelAsync();

            var failure = await Assert.ThrowsAsync<ArgumentNullException>(
                () => initialization.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.Equal("ConnectionOptions", failure.ParamName);
            Assert.False(connection.IsClosed);
            closeRelease.SetResult();
            await factory.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(connection.IsClosed);
            Assert.Equal(1, connection.CloseCount);
        }
        finally
        {
            closeRelease.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAdapter_PreservesDerivedInitializationOrderAndDoesNotRepeatInit(bool alreadyInitialized)
    {
        await using var connection = new TrackingConnection();
        using var host = CreateHost(connection, false, false, false, null);
        var factory = new RecordingFactory(host.Services);
        try
        {
            if (alreadyInitialized)
            {
                factory.Init();
            }

            Assert.Same(factory, await factory.CreateAdapter());
            Assert.Same(factory, await factory.CreateAdapter());
            Assert.Equal(new[] { "init", "client", "cache", "initialized", "partitions" }, factory.Calls);
        }
        finally
        {
            await factory.ShutdownAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Shutdown_PreservesProducerAndConnectionFailures(bool failProducer, bool failConnection)
    {
        await using var connection = new TrackingConnection();
        using var host = CreateHost(connection, false, true, true, null);
        var factory = Assert.IsType<EventHubAdapterFactory>(
            host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(ProviderName));
        factory.Init();
        var producerFailure = failProducer ? new InvalidOperationException("producer close failed") : null;
        var connectionFailure = failConnection ? new InvalidOperationException("connection close failed") : null;
        var producerField = typeof(EventHubAdapterFactory).GetField("producer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var producer = new FailingProducer(
            Assert.IsAssignableFrom<IEventHubProducer>(producerField.GetValue(factory)), producerFailure);
        producerField.SetValue(factory, producer);
        connection.CloseFailure = connectionFailure;
        try
        {
            var failure = await Record.ExceptionAsync(
                () => factory.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout, TestContext.Current.CancellationToken));
            if (failProducer && failConnection)
            {
                Assert.Equal(new[] { producerFailure, connectionFailure },
                    Assert.IsType<AggregateException>(failure).InnerExceptions);
            }
            else
            {
                Assert.Same(producerFailure ?? connectionFailure, failure);
            }

            Assert.Same(failure, await Record.ExceptionAsync(
                () => factory.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout, TestContext.Current.CancellationToken)));
            Assert.Equal(1, producer.CloseCount);
            Assert.Equal(1, connection.CloseCount);
        }
        finally
        {
            connection.CloseFailure = null;
        }
    }

    private static Task Initialize(PersistentStreamProvider provider, CancellationToken cancellationToken)
        => (Task)typeof(PersistentStreamProvider)
            .GetMethod("Init", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider, new object[] { cancellationToken })!;

    private static IHost CreateHost(
        TrackingConnection connection, bool genericRegistration, bool buffered, bool ownsConnection,
        Exception? lateFailure, bool failConstruction = false)
        => new HostBuilder()
            .ConfigureLogging(logging => logging.AddProvider(new InitializationLogger(lateFailure)))
            .UseOrleansClient(client =>
            {
                client.UseLocalhostClustering();
                if (genericRegistration)
                {
                    client.AddPersistentStreams(ProviderName, EventHubAdapterFactory.Create,
                        stream => stream.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
                }
                else
                {
                    client.AddEventHubStreams(ProviderName, (IClusterClientEventHubStreamConfigurator stream) =>
                        stream.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
                }

                client.Services.AddOptions<EventHubOptions>(ProviderName).Configure(options =>
                {
                    if (ownsConnection)
                    {
                        options.ConfigureEventHubConnection(_ => connection, "events", "$Default");
                    }
                    else
                    {
                        options.ConfigureEventHubConnection(connection, "$Default");
                    }

                    options.BufferedProducerOptions = buffered ? new EventHubBufferedProducerClientOptions() : null;
                    if (failConstruction)
                    {
                        // Connection acquisition succeeds; the SDK rejects these options when constructing the producer.
                        options.ConnectionOptions = null!;
                    }
                });
            }).Build();

    private sealed class TrackingConnection()
        : EventHubConnection("Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==", "events")
    {
        public Task CloseRelease { get; set; } = Task.CompletedTask;
        public Exception? CloseFailure { get; set; }
        public TaskCompletionSource CloseCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseCount { get; private set; }
        public override async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCount++;
            CloseCalled.TrySetResult();
            await CloseRelease;
            if (CloseFailure is { } failure)
            {
                throw failure;
            }

            await base.CloseAsync(cancellationToken);
        }
    }

    private sealed class FailingProducer(IEventHubProducer inner, Exception? failure) : IEventHubProducer
    {
        public int CloseCount { get; private set; }
        public Task SendAsync(EventData eventData, string partitionKey) => inner.SendAsync(eventData, partitionKey);
        public Task<string[]> GetPartitionIdsAsync() => inner.GetPartitionIdsAsync();
        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            await inner.CloseAsync(cancellationToken);
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private sealed class InitializationLogger(Exception? failure) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName)
            => failure is not null && categoryName == $"{typeof(EventHubAdapterFactory).FullName}.events"
                ? throw failure : this;
        public bool IsEnabled(LogLevel logLevel) => false;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private sealed class RecordingFactory(IServiceProvider services)
        : EventHubAdapterFactory(ProviderName, new EventHubOptions(), new EventHubReceiverOptions(),
            new EventHubStreamCachePressureOptions(), new StreamCacheEvictionOptions(), new StreamStatisticOptions(),
            ActivatorUtilities.CreateInstance<EventHubDataAdapter>(services), services,
            services.GetRequiredService<ILoggerFactory>(), services.GetRequiredService<IEnvironmentStatisticsProvider>())
    {
        public List<string> Calls { get; } = [];

        public override void Init()
        {
            Calls.Add("init");
            base.Init();
            Calls.Add("initialized");
        }

        protected override void InitEventHubClient() => Calls.Add("client");

        protected override IEventHubQueueCacheFactory CreateCacheFactory(EventHubStreamCachePressureOptions options)
        {
            Calls.Add("cache");
            return base.CreateCacheFactory(options);
        }

        protected override Task<string[]> GetPartitionIdsAsync()
        {
            Calls.Add("partitions");
            return Task.FromResult(new[] { "0" });
        }
    }
}
