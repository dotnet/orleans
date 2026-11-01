using System.Collections.Concurrent;
using System.Reflection;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Statistics;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using ServiceBus.Tests.MonitorTests;
using Xunit;

namespace ServiceBus.Tests;

[TestSuite("BVT")]
[TestProvider("EventHub")]
[TestArea("Streaming")]
[TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("BVT")]
public sealed class EventHubReceiverConnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartitionHandoff_ClosesOnlyDepartingReceiversConnection(bool ownsConnection)
    {
        await using var firstConnection = new TrackingConnection();
        await using var secondConnection = new TrackingConnection();
        var creations = 0;
        var firstSettings = CreateSettings(firstConnection, ownsConnection,
            () => ++creations == 1 ? firstConnection : secondConnection);
        var secondSettings = new EventHubPartitionSettings
        {
            Hub = firstSettings.Hub,
            Partition = "1",
            ReceiverOptions = firstSettings.ReceiverOptions
        };
        var first = CreateReceiver(firstSettings);
        var second = CreateReceiver(secondSettings);
        await first.Initialize(TestTimeout);
        await second.Initialize(TestTimeout);
        var firstClient = GetClient(first);
        var secondClient = GetClient(second);

        await first.Shutdown(TestTimeout);
        Assert.True(firstClient.IsClosed);
        Assert.False(secondClient.IsClosed);
        Assert.Equal(ownsConnection, firstConnection.IsClosed);
        Assert.False(secondConnection.IsClosed);
        Assert.Equal(0, secondConnection.CloseCount);

        await second.Shutdown(TestTimeout);
        Assert.True(secondClient.IsClosed);
        Assert.Equal(ownsConnection ? 2 : 0, creations);
        Assert.Equal(ownsConnection ? 1 : 0, firstConnection.CloseCount);
        Assert.Equal(ownsConnection ? 1 : 0, secondConnection.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownAndReinitialize_RespectConnectionOwnership(bool ownsConnection)
    {
        await using var firstConnection = new TrackingConnection();
        await using var secondConnection = new TrackingConnection();
        var creations = 0;
        var settings = CreateSettings(firstConnection, ownsConnection,
            () => ++creations == 1 ? firstConnection : secondConnection);
        var receiver = CreateReceiver(settings);

        await receiver.Initialize(TestTimeout);
        var firstClient = GetClient(receiver);
        await receiver.Shutdown(TestTimeout);
        Assert.True(firstClient.IsClosed);
        Assert.Equal(ownsConnection, firstConnection.IsClosed);
        Assert.Equal(ownsConnection ? 1 : 0, firstConnection.CloseCount);
        Assert.False(secondConnection.IsClosed);

        await receiver.Shutdown(TestTimeout);
        Assert.Equal(ownsConnection ? 1 : 0, firstConnection.CloseCount);

        await receiver.Initialize(TestTimeout);
        var secondClient = GetClient(receiver);
        Assert.NotSame(firstClient, secondClient);
        await receiver.Shutdown(TestTimeout);
        Assert.True(secondClient.IsClosed);
        Assert.Equal(ownsConnection ? 2 : 0, creations);
        Assert.Equal(ownsConnection ? 1 : 0, firstConnection.CloseCount);
        Assert.Equal(ownsConnection ? 1 : 0, secondConnection.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedConstructionAndRetry_RetainConnectionForShutdown(bool ownsConnection)
    {
        await using var firstConnection = new TrackingConnection();
        await using var secondConnection = new TrackingConnection();
        var creations = 0;
        var settings = CreateSettings(firstConnection, ownsConnection,
            () => ++creations == 1 ? firstConnection : secondConnection);
        settings.Partition = "";
        var receiver = CreateReceiver(settings);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => receiver.Initialize(TestTimeout));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => receiver.GetQueueMessagesAsync(1, TestContext.Current.CancellationToken));
        await receiver.Shutdown(TestTimeout);

        Assert.Equal(ownsConnection ? 1 : 0, creations);
        Assert.Equal(ownsConnection, firstConnection.IsClosed);
        Assert.Equal(ownsConnection ? 1 : 0, firstConnection.CloseCount);
        Assert.False(secondConnection.IsClosed);

        settings.Partition = "0";
        await receiver.Initialize(TestTimeout);
        var client = GetClient(receiver);
        await receiver.Shutdown(TestTimeout);
        Assert.True(client.IsClosed);
        Assert.Equal(ownsConnection ? 2 : 0, creations);
        Assert.Equal(ownsConnection ? 1 : 0, secondConnection.CloseCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Shutdown_WaitsForReceiverBeforeClosingOwnedConnection(bool ownsConnection, bool failClose)
    {
        await using var connection = new TrackingConnection();
        var settings = CreateSettings(connection, ownsConnection, () => connection);
        var receiver = CreateReceiver(settings);
        await receiver.Initialize(TestTimeout);
        var client = GetClient(receiver);
        var controlled = new ControlledReceiver(GetTransport(receiver));
        typeof(EventHubAdapterReceiver).GetField("receiver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(receiver, controlled);
        var expected = failClose ? new InvalidOperationException("receiver close failed") : null;
        controlled.CloseFailure = expected;
        connection.BeforeClose = () => Assert.True(client.IsClosed);

        var shutdown = receiver.Shutdown(TestTimeout);
        try
        {
            await controlled.CloseStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            Assert.False(client.IsClosed);
            Assert.Equal(0, connection.CloseCount);
            controlled.CloseRelease.SetResult();

            var failure = await Record.ExceptionAsync(
                () => shutdown.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.Same(expected, failure);
            Assert.True(client.IsClosed);
            Assert.Equal(ownsConnection, connection.IsClosed);
            Assert.Equal(ownsConnection ? 1 : 0, connection.CloseCount);
        }
        finally
        {
            controlled.CloseRelease.TrySetResult();
            connection.BeforeClose = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_CheckpointFailureStillClosesOwnedConnection(bool ownsConnection)
    {
        await using var connection = new TrackingConnection();
        var expected = new InvalidOperationException("checkpoint flush failed");
        var settings = CreateSettings(connection, ownsConnection, () => connection);
        var receiver = CreateReceiver(settings, expected);
        await receiver.Initialize(TestTimeout);
        var client = GetClient(receiver);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.Shutdown(TestTimeout));

        Assert.Same(expected, failure);
        Assert.True(client.IsClosed);
        Assert.Equal(ownsConnection, connection.IsClosed);
        Assert.Equal(ownsConnection ? 1 : 0, connection.CloseCount);
    }

    [Fact]
    public async Task Shutdown_DeadlineDoesNotCancelOwnedConnectionCleanup()
    {
        await using var connection = new TrackingConnection();
        var closeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = CreateReceiver(CreateSettings(connection, true, () => connection));
        await receiver.Initialize(TestTimeout);
        connection.CloseRelease = closeRelease.Task;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => receiver.Shutdown(TimeSpan.Zero).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.Equal(1, connection.CloseCount);
            await connection.CloseStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.False(connection.IsClosed);
            Assert.False(connection.CloseToken.CanBeCanceled);
            closeRelease.SetResult();
            await connection.CloseFinished.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.True(connection.IsClosed);
            Assert.Equal(1, connection.CloseCount);
        }
        finally
        {
            closeRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task Reinitialize_WhileOldConnectionCleanupIsPending_DoesNotCloseNewConnection()
    {
        await using var firstConnection = new TrackingConnection();
        await using var secondConnection = new TrackingConnection();
        var creations = 0;
        var receiver = CreateReceiver(CreateSettings(firstConnection, true,
            () => ++creations == 1 ? firstConnection : secondConnection));
        await receiver.Initialize(TestTimeout);
        var closeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstConnection.CloseRelease = closeRelease.Task;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => receiver.Shutdown(TimeSpan.Zero).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            await firstConnection.CloseStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.False(firstConnection.CloseOperation.IsCompleted);

            await receiver.Initialize(TestTimeout);
            var newClient = GetClient(receiver);
            Assert.Equal(2, creations);
            closeRelease.SetResult();
            await firstConnection.CloseOperation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.True(firstConnection.IsClosed);
            Assert.False(newClient.IsClosed);
            Assert.False(secondConnection.IsClosed);
            Assert.Equal(0, secondConnection.CloseCount);

            await receiver.Shutdown(TestTimeout);
            Assert.True(newClient.IsClosed);
            Assert.True(secondConnection.IsClosed);
            Assert.Equal(1, firstConnection.CloseCount);
            Assert.Equal(1, secondConnection.CloseCount);
        }
        finally
        {
            closeRelease.TrySetResult();
            await receiver.Shutdown(TestTimeout);
        }
    }

    [Fact]
    public async Task Shutdown_PropagatesOwnedConnectionCloseFailure()
    {
        await using var connection = new TrackingConnection();
        var receiver = CreateReceiver(CreateSettings(connection, true, () => connection));
        await receiver.Initialize(TestTimeout);
        var client = GetClient(receiver);
        var expected = new InvalidOperationException("connection close failed");
        connection.CloseFailure = expected;
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.Shutdown(TestTimeout));
            Assert.Same(expected, failure);
            Assert.True(client.IsClosed);
            Assert.Equal(1, connection.CloseCount);
        }
        finally
        {
            connection.CloseFailure = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_PreservesReceiverAndConnectionFailures(bool failFlush)
    {
        await using var connection = new TrackingConnection();
        var receiverFailure = new InvalidOperationException("receiver close failed");
        var connectionFailure = new InvalidOperationException("connection close failed");
        var flushFailure = failFlush ? new InvalidOperationException("checkpoint flush failed") : null;
        var logs = new CleanupLogger(2);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var receiver = CreateReceiver(CreateSettings(connection, true, () => connection), flushFailure, loggerFactory);
        await receiver.Initialize(TestTimeout);
        var controlled = new ControlledReceiver(GetTransport(receiver)) { CloseFailure = receiverFailure };
        controlled.CloseRelease.SetResult();
        typeof(EventHubAdapterReceiver).GetField("receiver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(receiver, controlled);
        connection.CloseFailure = connectionFailure;
        try
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => receiver.Shutdown(TestTimeout));
            var expected = flushFailure is null
                ? new[] { receiverFailure, connectionFailure }
                : new[] { flushFailure, receiverFailure, connectionFailure };
            Assert.Equal(expected, failure.Flatten().InnerExceptions);
            Assert.Equal(new[] { receiverFailure, connectionFailure }, logs.Errors.Select(entry => entry.Exception));
            Assert.Equal(1, connection.CloseCount);
        }
        finally
        {
            connection.CloseFailure = null;
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Shutdown_LogsCleanupFailuresEvenAfterItsWaitTimesOut(bool failReceiver, bool failConnection)
    {
        await using var connection = new TrackingConnection();
        var receiverFailure = failReceiver ? new InvalidOperationException("receiver close failed") : null;
        var connectionFailure = failConnection ? new InvalidOperationException("connection close failed") : null;
        var expected = new[] { receiverFailure, connectionFailure }.OfType<Exception>().ToArray();
        var logs = new CleanupLogger(expected.Length);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var receiver = CreateReceiver(CreateSettings(connection, true, () => connection), loggerFactory: loggerFactory);
        await receiver.Initialize(TestTimeout);
        var controlled = new ControlledReceiver(GetTransport(receiver))
        {
            CloseFailure = receiverFailure,
            CloseWithoutCancellation = true
        };
        controlled.CloseRelease.SetResult();
        typeof(EventHubAdapterReceiver).GetField("receiver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(receiver, controlled);
        var closeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.CloseRelease = closeRelease.Task;
        connection.CloseFailure = connectionFailure;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => receiver.Shutdown(TimeSpan.Zero).WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            await connection.CloseStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.False(connection.IsClosed);
            Assert.False(connection.CloseToken.CanBeCanceled);
            if (receiverFailure is not null)
            {
                Assert.Same(receiverFailure, Assert.Single(logs.Errors).Exception);
            }
            else
            {
                Assert.Empty(logs.Errors);
            }

            closeRelease.SetResult();
            var cleanupFailure = await Record.ExceptionAsync(
                () => connection.CloseOperation.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.Same(connectionFailure, cleanupFailure);
            await logs.AllErrorsLogged.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(expected, logs.Errors.Select(entry => entry.Exception));
            Assert.All(logs.Errors, entry => Assert.Contains("events-0", entry.Message));
        }
        finally
        {
            closeRelease.TrySetResult();
            connection.CloseFailure = null;
        }
    }

    private static EventHubPartitionSettings CreateSettings(
        EventHubConnection sharedConnection, bool ownsConnection, Func<EventHubConnection> createConnection)
    {
        var hub = new EventHubOptions();
        if (ownsConnection)
        {
            hub.ConfigureEventHubConnection(_ => createConnection(), "events", "$Default");
        }
        else
        {
            hub.ConfigureEventHubConnection(sharedConnection, "$Default");
        }

        return new EventHubPartitionSettings { Hub = hub, Partition = "0", ReceiverOptions = new() };
    }

    private static EventHubAdapterReceiver CreateReceiver(
        EventHubPartitionSettings settings, Exception? flushFailure = null, ILoggerFactory? loggerFactory = null)
    {
        return new EventHubAdapterReceiver(settings,
            (_, _, _) => new TestCache(),
            (_, _) => Task.FromResult<IStreamQueueCheckpointer<string>>(new TestCheckpointer(flushFailure)),
            loggerFactory ?? NullLoggerFactory.Instance,
            new EventHubReceiverMonitorForTesting(),
            new LoadSheddingOptions(),
            new EnvironmentStatisticsProvider());
    }

    private static IEventHubReceiver GetTransport(EventHubAdapterReceiver receiver)
        => Assert.IsAssignableFrom<IEventHubReceiver>(typeof(EventHubAdapterReceiver)
            .GetField("receiver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receiver));

    private static PartitionReceiver GetClient(EventHubAdapterReceiver receiver)
        => Assert.IsType<PartitionReceiver>(typeof(EventHubReceiverProxy)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(GetTransport(receiver)));

    private sealed class TestCheckpointer(Exception? flushFailure) : IStreamQueueCheckpointer<string>
    {
        public bool CheckpointExists => false;
        public Task<string> Load() => Task.FromResult(EventHubConstants.StartOfStream);
        public void Update(string offset, DateTime utcNow) => throw new NotSupportedException();
        public Task FlushAsync(CancellationToken cancellationToken)
            => flushFailure is null ? Task.CompletedTask : Task.FromException(flushFailure);
    }

    private sealed class TestCache : IEventHubQueueCache
    {
        public int GetMaxAddCount() => 1_000;
        public List<StreamPosition> Add(List<EventData> message, DateTime dequeueTimeUtc) => throw new NotSupportedException();
        public object GetCursor(StreamId streamId, StreamSequenceToken? sequenceToken) => throw new NotSupportedException();
        public bool TryGetNextMessage(object cursorObj, out IBatchContainer message) => throw new NotSupportedException();
        public void AddCachePressureMonitor(ICachePressureMonitor monitor) => throw new NotSupportedException();
        public void SignalPurge() => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class TrackingConnection()
        : EventHubConnection("Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==", "events")
    {
        public Action? BeforeClose { get; set; }
        public Exception? CloseFailure { get; set; }
        public Task CloseRelease { get; set; } = Task.CompletedTask;
        public Task CloseOperation { get; private set; } = Task.CompletedTask;
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CloseToken { get; private set; }
        public int CloseCount { get; private set; }

        public override Task CloseAsync(CancellationToken cancellationToken = default)
            => CloseOperation = CloseCoreAsync(cancellationToken);

        private async Task CloseCoreAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            CloseToken = cancellationToken;
            BeforeClose?.Invoke();
            CloseStarted.TrySetResult();
            await CloseRelease;
            if (CloseFailure is { } failure)
            {
                throw failure;
            }

            await base.CloseAsync(cancellationToken);
            CloseFinished.TrySetResult();
        }
    }

    private sealed class ControlledReceiver(IEventHubReceiver inner) : IEventHubReceiver
    {
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? CloseFailure { get; set; }
        public bool CloseWithoutCancellation { get; init; }

        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
            => inner.ReceiveAsync(maxCount, waitTime, CancellationToken.None);

        public Task CloseAsync() => CloseAsync(CancellationToken.None);

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            CloseStarted.TrySetResult();
            await CloseRelease.Task;
            await inner.CloseAsync(CloseWithoutCancellation ? CancellationToken.None : cancellationToken);
            if (CloseFailure is { } failure)
            {
                throw failure;
            }
        }

    }

    private sealed class CleanupLogger(int expectedErrorCount) : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(Exception Exception, string Message)> Errors { get; } = new();
        public TaskCompletionSource AllErrorsLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
            {
                Errors.Enqueue((exception, formatter(state, exception)));
                if (Errors.Count == expectedErrorCount)
                {
                    AllErrorsLogged.TrySetResult();
                }
            }
        }
    }
}
