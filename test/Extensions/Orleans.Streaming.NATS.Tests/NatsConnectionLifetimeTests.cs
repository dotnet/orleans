using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans;
using Orleans.Hosting;
using Orleans.Streaming.NATS;
using Orleans.Streaming.NATS.Hosting;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace NATS.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("NATS")]
[TestCategory("NATS")]
public sealed class NatsConnectionLifetimeTests
{
    private const string ProviderName = "connection-lifetime";
    private static readonly string[] ConnectionNames =
    [
        $"Orleans-{ProviderName}",
        $"Orleans-{ProviderName}-Producer-0",
        $"Orleans-{ProviderName}-Producer-1",
    ];

    [Fact]
    public async Task FailedInitialization_DisposesEveryConnectionAndAllowsRetry()
    {
        using var logs = new ConnectionLogs();
        var sockets = new FailingSocketFactory();
        sockets.Release.SetResult();
        using var host = CreateHost(logs, sockets);
        var factory = GetFactory(host);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await Assert.ThrowsAsync<NatsException>(() => factory.CreateAdapter());
            Assert.Equal(attempt, sockets.Attempts);
            AssertDisposed(logs, attempt);
        }

        await ((IAsyncDisposable)host).DisposeAsync();
        AssertDisposed(logs, 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PartialContextConstruction_DisposesCreatedConnections(int failedContext)
    {
        using var logs = new ConnectionLogs { FailOnContext = failedContext };
        var sockets = new FailingSocketFactory();
        using var host = CreateHost(logs, sockets);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => GetFactory(host).CreateAdapter());

        Assert.Same(logs.ConstructionFailure, exception);
        Assert.Equal(0, sockets.Attempts);
        Assert.Equal(ConnectionNames.Take(failedContext).Order(), logs.DisposedNames.Order());
        await ((IAsyncDisposable)host).DisposeAsync();
        Assert.Equal(failedContext, logs.DisposedNames.Count);
    }

    [Fact]
    public async Task UninitializedManager_DisposalPreventsOpeningConnections()
    {
        using var logs = new ConnectionLogs();
        var sockets = new FailingSocketFactory();
        await using var manager = new NatsConnectionManager(ProviderName, NullLoggerFactory.Instance, new NatsOptions
        {
            StreamName = "uninitialized",
            ProducerCount = 2,
            NatsClientOptions = NatsOpts.Default with { LoggerFactory = logs, SocketConnectionFactory = sockets },
        });

        await manager.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => { _ = manager.Initialize(TestContext.Current.CancellationToken); });
        Assert.Equal(0, sockets.Attempts);
        Assert.Empty(logs.DisposedNames);
    }

    [Fact]
    public async Task ConcurrentDisposal_InterruptsConnectAndDrainsInitialization()
    {
        using var logs = new ConnectionLogs();
        var sockets = new FailingSocketFactory { WaitForCancellation = true };
        using var host = CreateHost(logs, sockets, retryOnInitialConnect: true);
        var factory = GetFactory(host);
        var creating = factory.CreateAdapter();

        try
        {
            await sockets.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var firstDisposal = factory.DisposeAsync().AsTask();
            var secondDisposal = factory.DisposeAsync().AsTask();
            await sockets.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Same(firstDisposal, secondDisposal);
            Assert.False(firstDisposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.CreateAdapter());
            sockets.Release.SetResult();

            await firstDisposal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => creating);
            await factory.DisposeAsync();
            AssertDisposed(logs, 1);
        }
        finally
        {
            sockets.Release.TrySetResult();
            await factory.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredFactory_DisposesEverySuccessfulAdapterPool(bool silo)
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Nats Server is not available");
        }

        using var logs = new ConnectionLogs();
        using var host = CreateHost(logs, silo: silo);
        var factory = GetFactory(host);
        var first = await factory.CreateAdapter();
        var second = await factory.CreateAdapter();
        Assert.NotSame(first, second);

        await first.QueueMessageBatchAsync(StreamId.Create("lifetime", "first"), new[] { 1 }, null!, null!);
        await second.QueueMessageBatchAsync(StreamId.Create("lifetime", "second"), new[] { 2 }, null!, null!);
        Assert.Empty(logs.DisposedNames);

        await ((IAsyncDisposable)host).DisposeAsync();

        AssertDisposed(logs, 2);
        await factory.DisposeAsync();
        AssertDisposed(logs, 2);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.CreateAdapter());
    }

    private static NatsAdapterFactory GetFactory(IHost host) =>
        Assert.IsType<NatsAdapterFactory>(host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(ProviderName));

    private static IHost CreateHost(ConnectionLogs logs, FailingSocketFactory? sockets = null, bool retryOnInitialConnect = false, bool silo = false)
    {
        var streamName = $"lifetime-{Guid.NewGuid():N}";
        void Configure(NatsOptions options)
        {
            options.StreamName = streamName;
            options.ProducerCount = 2;
            options.NatsClientOptions = NatsTestConstants.NatsClientOptions with
            {
                Name = ConnectionNames[0],
                LoggerFactory = logs,
                SocketConnectionFactory = sockets,
                RetryOnInitialConnect = retryOnInitialConnect,
                ConnectTimeout = TimeSpan.FromSeconds(30),
            };
        }

        var builder = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders());
        if (silo)
        {
            builder.UseOrleans(builder => builder.UseLocalhostClustering().AddNatsStreams(ProviderName, Configure));
        }
        else
        {
            builder.UseOrleansClient(builder => builder.UseLocalhostClustering().AddNatsStreams(ProviderName, Configure));
        }

        return builder.Build();
    }

    private static void AssertDisposed(ConnectionLogs logs, int poolCount) =>
        Assert.Equal(Enumerable.Range(0, poolCount).SelectMany(_ => ConnectionNames).Order(), logs.DisposedNames.Order());

    private sealed class FailingSocketFactory : INatsSocketConnectionFactory
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<INatsSocketConnection> ConnectAsync(Uri uri, NatsOpts opts, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            Entered.TrySetResult();
            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.SetResult();
                    await Release.Task;
                    throw;
                }
            }

            await Release.Task.WaitAsync(cancellationToken);
            throw new NatsException("Injected connection failure.");
        }
    }

    private sealed class ConnectionLogs : ILoggerFactory
    {
        private int _contexts;
        public int FailOnContext { get; init; }
        public Exception ConstructionFailure { get; } = new InvalidOperationException("Injected context construction failure.");
        public ConcurrentBag<string> DisposedNames { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            if (categoryName == typeof(NatsJSContext).FullName && Interlocked.Increment(ref _contexts) == FailOnContext)
            {
                throw ConstructionFailure;
            }

            return new ConnectionLogger(this);
        }

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() { }

        private sealed class ConnectionLogger(ConnectionLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> values
                    && values.Any(entry => entry.Key == "{OriginalFormat}" && Equals(entry.Value, "Disposing connection {Name}")))
                {
                    owner.DisposedNames.Add((string)values.Single(entry => entry.Key == "Name").Value!);
                }
            }
        }
    }
}
