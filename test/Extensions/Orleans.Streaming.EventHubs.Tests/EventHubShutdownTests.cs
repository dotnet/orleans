using System.Collections.Concurrent;
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
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using Orleans.TestingHost;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class EventHubShutdownTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string ConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostShutdown_DrainsBothRegistrationsAndPropagatesFlushFailures(bool genericRegistration, bool failFlush)
    {
        var silos = new ConcurrentDictionary<string, TrackingFactory>();
        var clients = new ConcurrentDictionary<string, TrackingFactory>();
        var logs = new ErrorCollector();
        var builder = CreateBuilder(genericRegistration, silos, clients, logs);
        var cluster = builder.Build();
        await using var cleanup = new ClusterCleanup(cluster, silos, clients);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        await VerifyShutdown(cluster.Client.ServiceProvider.GetRequiredService<IHost>(), clients);
        await VerifyShutdown(Assert.Single(cluster.Silos).SiloHost, silos);

        async Task VerifyShutdown(IHost host, ConcurrentDictionary<string, TrackingFactory> factories)
        {
            Assert.Equal(2, factories.Count);
            var sends = factories.Select(pair => (
                Factory: pair.Value,
                Send: host.Services.GetRequiredKeyedService<IStreamProvider>(pair.Key)
                    .GetStream<int>(StreamId.Create("shutdown", pair.Key)).OnNextAsync(1))).ToArray();
            await Task.WhenAll(sends.Select(item => item.Factory.Client.Enqueued.Task)).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var stop = host.StopAsync(TestContext.Current.CancellationToken);
            await Task.WhenAll(sends.Select(item => item.Factory.ShutdownCalled.Task)).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted);

            foreach (var (factory, send) in sends)
            {
                Assert.Equal(1, factory.CreateCount);
                Assert.Equal(1, factory.ShutdownCount);
                Assert.False(send.IsCompleted);
                Assert.False(factory.Client.CloseCalled.Task.IsCompleted);
                factory.Client.EnqueueRelease.SetResult();
            }

            await Task.WhenAll(sends.Select(item => item.Factory.Client.CloseCalled.Task)).WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted);
            foreach (var (factory, send) in sends)
            {
                var failure = new InvalidOperationException($"flush failed: {factory.Name}");
                if (failFlush)
                {
                    factory.Client.CloseRelease.SetException(failure);
                    Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => send.WaitAsync(Timeout, TestContext.Current.CancellationToken)));
                    Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                        () => factory.Inner.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout, TestContext.Current.CancellationToken)));
                }
                else
                {
                    Assert.False(send.IsCompleted);
                    factory.Client.Acknowledge();
                    await send.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    Assert.False(stop.IsCompleted);
                    factory.Client.CloseRelease.SetResult();
                }
            }

            await stop.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            foreach (var (factory, _) in sends)
            {
                Assert.Equal(factory.OwnsConnection, factory.Connection.IsClosed);
                Assert.Equal(1, factory.Client.CloseCount);
                await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                    factory.Inner.QueueMessageBatchAsync(StreamId.Create("shutdown", "late"), new[] { 2 }, null, null));
                await factory.Connection.CloseAsync(TestContext.Current.CancellationToken);
            }

            if (failFlush)
            {
                foreach (var factory in factories.Values)
                {
                    Assert.Contains(logs.Errors, error => error.Message == $"flush failed: {factory.Name}");
                }
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostShutdown_CanceledWaitContinuesDraining(bool genericRegistration, bool failFlush)
    {
        var silos = new ConcurrentDictionary<string, TrackingFactory>();
        var clients = new ConcurrentDictionary<string, TrackingFactory>();
        var logs = new ErrorCollector();
        var builder = CreateBuilder(genericRegistration, silos, clients, logs);
        var cluster = builder.Build();
        await using var cleanup = new ClusterCleanup(cluster, silos, clients);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var host = cluster.Client.ServiceProvider.GetRequiredService<IHost>();
        var factories = clients.Values.ToArray();
        var sends = factories.Select(factory =>
            factory.Inner.QueueMessageBatchAsync(StreamId.Create("shutdown", factory.Name), new[] { 1 }, null, null)).ToArray();
        await Task.WhenAll(factories.Select(factory => factory.Client.Enqueued.Task)).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var stop = host.StopAsync(cancellation.Token);
        await Task.WhenAll(factories.Select(factory => factory.ShutdownCalled.Task)).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        try
        {
            await stop.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        Assert.All(sends, send => Assert.False(send.IsCompleted));

        for (var i = 0; i < factories.Length; i++)
        {
            var factory = factories[i];
            Assert.False(factory.Connection.IsClosed);
            factory.Client.EnqueueRelease.SetResult();
            await factory.Client.CloseCalled.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            if (failFlush)
            {
                var failure = new InvalidOperationException($"late flush failed: {factory.Name}");
                factory.Client.CloseRelease.SetException(failure);
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => sends[i].WaitAsync(Timeout, TestContext.Current.CancellationToken)));
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => factory.Inner.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout, TestContext.Current.CancellationToken)));
            }
            else
            {
                factory.Client.Acknowledge();
                factory.Client.CloseRelease.SetResult();
                await factory.Inner.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout, TestContext.Current.CancellationToken);
                await sends[i].WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }

            Assert.Equal(factory.OwnsConnection, factory.Connection.IsClosed);
            Assert.Equal(1, factory.Client.CloseCount);
            await factory.Connection.CloseAsync(TestContext.Current.CancellationToken);
        }

        // Await the provider's detached task so the error-log assertions observe completed cleanup.
        foreach (var factory in factories)
        {
            var provider = host.Services.GetRequiredKeyedService<IStreamProvider>(factory.Name);
            var shutdown = (Task)typeof(PersistentStreamProvider)
                .GetField("adapterShutdownTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
            if (failFlush)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => shutdown.WaitAsync(Timeout, TestContext.Current.CancellationToken));
                Assert.Contains(logs.Errors, error => error.Message == $"late flush failed: {factory.Name}");
            }
            else
            {
                await shutdown.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }
        }

        foreach (var factory in silos.Values)
        {
            factory.Client.CloseRelease.SetResult();
        }
    }

    [Fact]
    public async Task DirectProducerShutdown_ClosesProducerButNotBorrowedConnection()
    {
        await using var connection = new EventHubConnection(ConnectionString, "events");
        await using var client = new EventHubProducerClient(connection);
        var producer = new EventHubProducer(client);

        await producer.CloseAsync(CancellationToken.None);

        Assert.True(client.IsClosed);
        Assert.False(connection.IsClosed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostStartup_AdapterFailureClosesEachFactoryOnce(bool genericRegistration, bool failClient)
    {
        var silos = new ConcurrentDictionary<string, TrackingFactory>();
        var clients = new ConcurrentDictionary<string, TrackingFactory>();
        var expected = new InvalidOperationException("partition discovery failed");
        var builder = CreateBuilder(genericRegistration, silos, clients, new ErrorCollector(),
            siloFailure: failClient ? null : expected, clientFailure: failClient ? expected : null);
        var cluster = builder.Build();
        await using var cleanup = new ClusterCleanup(cluster, silos, clients);

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.DeployAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken)));

        var factories = failClient ? clients : silos;
        Assert.Equal(2, factories.Count);
        foreach (var factory in factories.Values)
        {
            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(1, factory.ShutdownCount);
            Assert.Equal(1, factory.Client.CloseCount);
            Assert.Equal(factory.OwnsConnection, factory.Connection.IsClosed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostShutdown_WaitsForDetachedReceiverDrainAndClosesAfterDrainFailure(bool genericRegistration)
    {
        var silos = new ConcurrentDictionary<string, TrackingFactory>();
        var clients = new ConcurrentDictionary<string, TrackingFactory>();
        var logs = new ErrorCollector();
        var builder = CreateBuilder(genericRegistration, silos, clients, logs);
        var cluster = builder.Build();
        await using var cleanup = new ClusterCleanup(cluster, silos, clients);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var host = Assert.Single(cluster.Silos).SiloHost;
        var manager = new DrainingManager();
        cleanup.Drains.Add(manager.Drain);
        var provider = host.Services.GetRequiredKeyedService<IStreamProvider>("owned");
        typeof(PersistentStreamProvider).GetField("pullingAgentManager", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(provider, manager);
        foreach (var factory in silos.Values.Concat(clients.Values))
        {
            factory.Client.CloseRelease.SetResult();
        }

        using var cancellation = new CancellationTokenSource();
        var stop = host.StopAsync(cancellation.Token);
        await manager.StopCalled.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        try
        {
            await stop.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Assert.False(silos["owned"].ShutdownCalled.Task.IsCompleted);
        var failure = new InvalidOperationException("receiver drain failed");
        manager.Drain.SetException(failure);
        await silos["owned"].ShutdownCalled.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await silos["owned"].Inner.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(silos["owned"].Connection.IsClosed);
        Assert.Contains(logs.Errors, exception => ReferenceEquals(exception, failure));
    }

    private static InProcessTestClusterBuilder CreateBuilder(
        bool genericRegistration,
        ConcurrentDictionary<string, TrackingFactory> silos,
        ConcurrentDictionary<string, TrackingFactory> clients,
        ErrorCollector logs,
        Exception? siloFailure = null,
        Exception? clientFailure = null)
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.ConfigureHost(host => host.Logging.AddProvider(logs));
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddMemoryGrainStorage("PubSubStore");
            foreach (var name in new[] { "owned", "shared" })
            {
                if (genericRegistration)
                {
                    silo.AddPersistentStreams(name, EventHubAdapterFactory.Create,
                        stream => stream.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
                }
                else
                {
                    silo.AddEventHubStreams(name, stream =>
                    {
                        stream.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                        stream.UseGrainCheckpointer();
                    });
                }

                ConfigureFactory(silo.Services, name, silos, siloFailure, siloFailure is not null || clientFailure is not null);
            }
        });
        builder.ConfigureClient(client =>
        {
            foreach (var name in new[] { "owned", "shared" })
            {
                if (genericRegistration)
                {
                    client.AddPersistentStreams(name, EventHubAdapterFactory.Create,
                        stream => stream.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
                }
                else
                {
                    client.AddEventHubStreams(name, (IClusterClientEventHubStreamConfigurator stream) =>
                        stream.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
                }

                ConfigureFactory(client.Services, name, clients, clientFailure, siloFailure is not null || clientFailure is not null);
            }
        });
        return builder;
    }

    private static void ConfigureFactory(
        IServiceCollection services,
        string name,
        ConcurrentDictionary<string, TrackingFactory> factories,
        Exception? initializationFailure,
        bool releaseClose)
    {
        var connection = new EventHubConnection(ConnectionString, "events");
        services.AddOptions<EventHubOptions>(name).Configure(options =>
        {
            if (name == "owned")
            {
                options.ConfigureEventHubConnection(_ => connection, "events", "$Default");
            }
            else
            {
                options.ConfigureEventHubConnection(connection, "$Default");
            }

            options.BufferedProducerOptions = new EventHubBufferedProducerClientOptions();
        });
        var registration = services.Last(service => service.IsKeyedService
            && service.ServiceType == typeof(IQueueAdapterFactory) && Equals(service.ServiceKey, name));
        services.AddKeyedSingleton<IQueueAdapterFactory>(name, (provider, key) =>
        {
            var factory = Assert.IsType<EventHubAdapterFactory>(registration.KeyedImplementationFactory!(provider, key));
            var tracked = new TrackingFactory(name, factory, connection, name == "owned", initializationFailure, releaseClose);
            Assert.True(factories.TryAdd(name, tracked));
            return tracked;
        });
    }

    private sealed class TrackingFactory(
        string name,
        EventHubAdapterFactory inner,
        EventHubConnection connection,
        bool ownsConnection,
        Exception? initializationFailure,
        bool releaseClose) : IQueueAdapterFactory
    {
        public string Name => name;
        public EventHubAdapterFactory Inner => inner;
        public BufferedClient Client { get; private set; } = null!;
        public EventHubConnection Connection => connection;
        public bool OwnsConnection => ownsConnection;
        public int CreateCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public TaskCompletionSource ShutdownCalled { get; } = NewCompletion();

        public Task<IQueueAdapter> CreateAdapter() => CreateAdapter(CancellationToken.None);

        public Task<IQueueAdapter> CreateAdapter(CancellationToken cancellationToken)
        {
            CreateCount++;
            if (Client is null)
            {
                inner.Init();
                var producerField = typeof(EventHubAdapterFactory).GetField("producer", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var originalProducer = Assert.IsAssignableFrom<IEventHubProducer>(producerField.GetValue(inner));
                Client = new BufferedClient(originalProducer) { InitializationFailure = initializationFailure };
                if (releaseClose)
                {
                    Client.CloseRelease.SetResult();
                }
                producerField.SetValue(inner, new AcknowledgedEventHubProducer(Client));
                typeof(EventHubAdapterFactory).GetProperty(nameof(EventHubAdapterFactory.Direction))!
                    .SetValue(inner, StreamProviderDirection.WriteOnly);
            }
            return inner.CreateAdapter();
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCount++;
            Assert.False(cancellationToken.CanBeCanceled);
            var shutdown = inner.ShutdownAsync(cancellationToken);
            ShutdownCalled.TrySetResult();
            return shutdown;
        }

        public IQueueAdapterCache GetQueueAdapterCache() => inner.GetQueueAdapterCache();
        public IStreamQueueMapper GetStreamQueueMapper() => inner.GetStreamQueueMapper();
        public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) => inner.GetDeliveryFailureHandler(queueId);
    }

    private sealed class BufferedClient(IEventHubProducer originalProducer) : IBufferedEventHubClient
    {
        private EventData? _event;
        public event Action<IReadOnlyList<EventData>>? BatchSucceeded;
        public event Action<IReadOnlyList<EventData>, Exception>? BatchFailed { add { } remove { } }
        public TaskCompletionSource Enqueued { get; } = NewCompletion();
        public TaskCompletionSource EnqueueRelease { get; } = NewCompletion();
        public TaskCompletionSource CloseCalled { get; } = NewCompletion();
        public TaskCompletionSource CloseRelease { get; } = NewCompletion();
        public int CloseCount { get; private set; }
        public Exception? InitializationFailure { get; init; }

        public Task EnqueueEventAsync(EventData eventData, string partitionKey)
        {
            _event = eventData;
            Enqueued.TrySetResult();
            return EnqueueRelease.Task;
        }

        public Task<string[]> GetPartitionIdsAsync() => InitializationFailure is { } exception
            ? Task.FromException<string[]>(exception)
            : Task.FromResult(new[] { "0" });

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            Assert.False(cancellationToken.CanBeCanceled);
            CloseCalled.TrySetResult();
            try
            {
                await CloseRelease.Task;
            }
            finally
            {
                await originalProducer.CloseAsync(cancellationToken);
            }
        }

        public void Acknowledge()
        {
            if (_event is { } eventData)
            {
                BatchSucceeded?.Invoke([eventData]);
            }
        }
    }

    private sealed class ErrorCollector : ILoggerProvider, ILogger
    {
        public ConcurrentBag<Exception> Errors { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Errors.Add(exception);
            }
        }
    }

    private sealed class DrainingManager : IPersistentStreamPullingManager
    {
        public TaskCompletionSource StopCalled { get; } = NewCompletion();
        public TaskCompletionSource Drain { get; } = NewCompletion();

        public Task Stop(CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            StopCalled.TrySetResult();
            return Drain.Task;
        }

        public Task Initialize(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAgents(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAgents(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<object?> ExecuteCommand(PersistentStreamProviderCommand command, object? arg, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ClusterCleanup(
        InProcessTestCluster cluster,
        ConcurrentDictionary<string, TrackingFactory> silos,
        ConcurrentDictionary<string, TrackingFactory> clients) : IAsyncDisposable
    {
        public List<TaskCompletionSource> Drains { get; } = [];

        public async ValueTask DisposeAsync()
        {
            foreach (var drain in Drains)
            {
                drain.TrySetResult();
            }

            foreach (var factory in silos.Values.Concat(clients.Values))
            {
                factory.Client.EnqueueRelease.TrySetResult();
                factory.Client.Acknowledge();
                factory.Client.CloseRelease.TrySetResult();
            }

            await cluster.DisposeAsync();
            foreach (var factory in silos.Values.Concat(clients.Values))
            {
                await factory.Connection.CloseAsync();
            }
        }
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
