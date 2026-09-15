using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Networking.Shared;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using TestExtensions;
using Xunit;

namespace Orleans.Core.Tests.Networking;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class ConnectionManagerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Close_WaitsForAdmittedUnpublishedEstablishment()
    {
        await using var rig = new TestRig();
        var context = new QueuedSynchronizationContext();
        var acquisition = context.GetConnection(rig.Manager, rig.Address);

        var closing = rig.Manager.Close(CancellationToken.None);

        Assert.False(closing.IsCompleted);
        Assert.False(rig.Manager.Closed.IsCompleted);
        Assert.True(rig.ShutdownSource.Token.IsCancellationRequested);
        Assert.Equal(0, rig.Factory.AttemptCount);

        context.RunNext();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => acquisition.WaitAsync(TestTimeout));
        Assert.Equal("Shutting down", error.Message);
        await closing.WaitAsync(TestTimeout);

        Assert.True(rig.Manager.Closed.IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => rig.ShutdownSource.Token);
        Assert.Equal(0, rig.Factory.AttemptCount);
    }

    [Fact]
    public async Task Close_DisposesConnectionCreatedAfterCancellation()
    {
        await using var rig = new TestRig();
        var acquisition = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var closing = rig.Manager.Close(CancellationToken.None);
        var connection = rig.CreateConnection(blockDisposal: true);

        Assert.True(attempt.CancellationToken.IsCancellationRequested);
        attempt.Completion.SetResult(connection);
        await connection.TransportContext.Disposing.Task.WaitAsync(TestTimeout);

        Assert.False(acquisition.IsCompleted);
        Assert.False(closing.IsCompleted);
        Assert.True(rig.ShutdownSource.Token.IsCancellationRequested);
        Assert.False(connection.Started.Task.IsCompleted);
        Assert.Equal(0, rig.Manager.ConnectionCount);

        connection.TransportContext.ReleaseDisposal.TrySetResult();
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => acquisition.WaitAsync(TestTimeout));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        await closing.WaitAsync(TestTimeout);

        Assert.Equal(1, connection.TransportContext.DisposeCount);
        Assert.False(connection.Started.Task.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => rig.ShutdownSource.Token);
    }

    [Fact]
    public async Task Close_DrainsPublicationFromPreviouslyAdmittedProducer()
    {
        using var logger = new PausedShutdownLogger();
        await using var rig = new TestRig(logger);
        var acquisition = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var closing = Task.Run(() => rig.Manager.Close(CancellationToken.None));
        try
        {
            await logger.ShutdownEntered.Task.WaitAsync(TestTimeout);
            var rejectedAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12346), 1);
            await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Manager.GetConnection(rejectedAddress).AsTask());

            var connection = rig.CreateConnection();
            attempt.Completion.SetResult(connection);
            Assert.Same(connection, await acquisition.WaitAsync(TestTimeout));
            Assert.True(connection.Initialized.IsCompletedSuccessfully);
            Assert.Equal(1, rig.Manager.ConnectionCount);
            Assert.False(closing.IsCompleted);
            Assert.False(rig.ShutdownSource.Token.IsCancellationRequested);
        }
        finally
        {
            logger.ReleaseShutdown.Set();
        }

        await closing.WaitAsync(TestTimeout);
        Assert.Equal(0, rig.Manager.ConnectionCount);
        Assert.Throws<ObjectDisposedException>(() => rig.ShutdownSource.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationFailure_WaitsForRunnerCleanup(bool shutdown)
    {
        await using var rig = new TestRig();
        var acquisition = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var connection = rig.CreateConnection(blockInitialization: true, blockCleanup: true);
        attempt.Completion.SetResult(connection);
        await connection.Started.Task.WaitAsync(TestTimeout);
        var failure = new ConnectionAbortedException("Initialization failed in the test");
        var closing = shutdown ? rig.Manager.Close(CancellationToken.None) : connection.CloseAsync(failure);

        await connection.CleanupStarted.Task.WaitAsync(TestTimeout);
        Assert.False(acquisition.IsCompleted);
        Assert.Equal(shutdown, rig.ShutdownSource.Token.IsCancellationRequested);
        if (shutdown)
        {
            Assert.False(closing.IsCompleted);
            Assert.False(rig.Manager.Closed.IsCompleted);
        }

        connection.ReleaseCleanup.TrySetResult();
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => acquisition.WaitAsync(TestTimeout));
        if (shutdown)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        }
        else
        {
            Assert.Same(failure, error.InnerException);
        }

        await closing.WaitAsync(TestTimeout);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
        Assert.Equal(0, rig.Manager.ConnectionCount);
        if (!shutdown)
        {
            var retry = await Assert.ThrowsAsync<ConnectionFailedException>(() => rig.Manager.GetConnection(rig.Address).AsTask());
            Assert.Contains("will retry after", retry.Message);
            Assert.Equal(1, rig.Factory.AttemptCount);
        }
    }

    [Fact]
    public async Task Close_CanceledHostWaitPreservesResourcesForUnwindingRunner()
    {
        await using var rig = new TestRig();
        var acquisition = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var connection = rig.CreateConnection(blockCleanup: true);
        attempt.Completion.SetResult(connection);
        Assert.Same(connection, await acquisition.WaitAsync(TestTimeout));
        using var hostCancellation = new CancellationTokenSource();

        var closing = rig.Manager.Close(hostCancellation.Token);
        await connection.CleanupStarted.Task.WaitAsync(TestTimeout);
        Assert.Equal(0, rig.Manager.ConnectionCount);
        Assert.False(closing.IsCompleted);
        hostCancellation.Cancel();
        await closing.WaitAsync(TestTimeout);

        Assert.True(rig.Manager.Closed.IsCompletedSuccessfully);
        Assert.True(rig.ShutdownSource.Token.IsCancellationRequested);
        Assert.False(connection.ReleaseCleanup.Task.IsCompleted);

        connection.ReleaseCleanup.TrySetResult();
        await rig.Manager.Close(CancellationToken.None).WaitAsync(TestTimeout);
        Assert.Throws<ObjectDisposedException>(() => rig.ShutdownSource.Token);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
    }

    [Fact]
    public async Task GetConnection_CoalescesAttemptsAndReusesInitializedConnection()
    {
        await using var rig = new TestRig();
        var first = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var context = new QueuedSynchronizationContext();
        var second = context.GetConnection(rig.Manager, rig.Address);
        context.RunNext();
        Assert.False(second.IsCompleted);
        Assert.Equal(1, rig.Factory.AttemptCount);

        var connection = rig.CreateConnection();
        attempt.Completion.SetResult(connection);
        Assert.Same(connection, await first.WaitAsync(TestTimeout));
        Assert.Same(connection, await second.WaitAsync(TestTimeout));
        var reused = rig.Manager.GetConnection(rig.Address);
        Assert.True(reused.IsCompletedSuccessfully);
        Assert.Same(connection, await reused);
        Assert.True(rig.Manager.TryGetConnection(rig.Address, out var existing));
        Assert.Same(connection, existing);
        Assert.Equal(1, rig.Factory.AttemptCount);

        await rig.Manager.CloseAsync(rig.Address).WaitAsync(TestTimeout);
        Assert.False(rig.Manager.TryGetConnection(rig.Address, out _));
        Assert.False(rig.Manager.Closed.IsCompleted);
        Assert.False(rig.ShutdownSource.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task OnConnected_RejectsInboundPublicationAfterClose()
    {
        await using var rig = new TestRig();
        await rig.Manager.Close(CancellationToken.None).WaitAsync(TestTimeout);
        var connection = rig.CreateConnection();

        var error = Assert.Throws<OperationCanceledException>(() => rig.Manager.OnConnected(rig.Address, connection));

        Assert.Equal("Shutting down", error.Message);
        Assert.Equal(0, rig.Manager.ConnectionCount);
        Assert.False(rig.Manager.TryGetConnection(rig.Address, out _));
        Assert.True(connection.IsValid);
        Assert.Equal(0, connection.TransportContext.DisposeCount);
    }

    [Fact]
    public async Task Run_ClosesQueuedConnectionBeforeInvokingMiddleware()
    {
        await using var rig = new TestRig();
        var connection = rig.CreateConnection();

        await connection.CloseAsync(exception: null).WaitAsync(TestTimeout);
        await connection.Run().WaitAsync(TestTimeout);

        Assert.False(connection.Started.Task.IsCompleted);
        Assert.False(connection.IsValid);
        Assert.True(connection.Initialized.IsFaulted);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
    }

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly ConcurrentBag<TestConnection> _connections = [];
        private readonly SharedMemoryPool _memoryPool = new();
        private readonly ConnectionCommon _shared;
        private readonly ConnectionDelegate _middleware;

        public TestRig(ILogger<ConnectionManager>? logger = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMetrics();
            services.AddSerializer(builder => builder.AddAssembly(typeof(Connection).Assembly));
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<MessagingInstruments>();
            services.AddSingleton<MessagingProcessingInstruments>();
            services.AddSingleton(_memoryPool);
            services.AddSingleton<MessagingOptions>(new ClientMessagingOptions());
            services.AddTransient<MessageSerializer>();
            _services = services.BuildServiceProvider();
            var instruments = _services.GetRequiredService<MessagingInstruments>();
            _shared = new ConnectionCommon(
                _services,
                null!,
                new MessagingTrace(NullLoggerFactory.Instance, instruments, _services.GetRequiredService<MessagingProcessingInstruments>()),
                _services.GetRequiredService<OrleansInstruments>(),
                instruments,
                NullLogger<Connection>.Instance,
                new NoOpMessageStatisticsSink());
            var builder = new ConnectionBuilder(_services);
            builder.Use(next => context =>
            {
                context.Features.Set<IUnderlyingTransportFeature>(new UnderlyingConnectionTransportFeature { Transport = context.Transport });
                return next(context);
            });
            Connection.ConfigureBuilder(builder);
            _middleware = builder.Build();
            var options = Options.Create(new ConnectionOptions
            {
                OpenConnectionTimeout = System.Threading.Timeout.InfiniteTimeSpan,
                ConnectionRetryDelay = TimeSpan.FromMinutes(1)
            });
            Factory = new TestConnectionFactory(_services, options);
            Manager = new ConnectionManager(options, Factory, logger ?? NullLogger<ConnectionManager>.Instance);
            ShutdownSource = (CancellationTokenSource)typeof(ConnectionManager)
                .GetField("shutdownCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Manager)!;
        }

        public SiloAddress Address { get; } = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12345), 1);
        public ConnectionManager Manager { get; }
        public TestConnectionFactory Factory { get; }
        public CancellationTokenSource ShutdownSource { get; }

        public TestConnection CreateConnection(bool blockInitialization = false, bool blockCleanup = false, bool blockDisposal = false)
        {
            var context = new TestConnectionContext(blockDisposal);
            var connection = new TestConnection(context, _middleware, _shared, Manager, Address, blockInitialization, blockCleanup);
            _connections.Add(connection);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections)
            {
                connection.ReleaseCleanup.TrySetResult();
                connection.TransportContext.ReleaseDisposal.TrySetResult();
                await connection.CloseAsync(exception: null).WaitAsync(TestTimeout);
            }

            Factory.FailPendingAttempts();
            await Manager.Close(CancellationToken.None).WaitAsync(TestTimeout);
            await _services.DisposeAsync();
            _memoryPool.Pool.Dispose();
        }
    }

    private sealed class TestConnectionFactory(IServiceProvider services, IOptions<ConnectionOptions> options)
        : ConnectionFactory(null!, services, options)
    {
        private readonly Channel<Attempt> _attempts = Channel.CreateUnbounded<Attempt>();
        private readonly ConcurrentBag<Attempt> _allAttempts = [];
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public override ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
        {
            var attempt = new Attempt(cancellationToken);
            _allAttempts.Add(attempt);
            Interlocked.Increment(ref _attemptCount);
            Assert.True(_attempts.Writer.TryWrite(attempt));
            return new(attempt.Completion.Task);
        }

        public Task<Attempt> NextAttempt() => _attempts.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public void FailPendingAttempts()
        {
            foreach (var attempt in _allAttempts)
            {
                attempt.Completion.TrySetException(new ConnectionAbortedException("Test teardown"));
            }
        }

        protected override Connection CreateConnection(SiloAddress address, ConnectionContext context) => throw new NotSupportedException();
    }

    private sealed class Attempt(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<Connection> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestConnection : Connection
    {
        private readonly ConnectionManager _manager;
        private readonly SiloAddress _address;
        private readonly bool _blockInitialization;

        public TestConnection(
            TestConnectionContext context,
            ConnectionDelegate middleware,
            ConnectionCommon shared,
            ConnectionManager manager,
            SiloAddress address,
            bool blockInitialization,
            bool blockCleanup) : base(context, middleware, shared)
        {
            TransportContext = context;
            _manager = manager;
            _address = address;
            _blockInitialization = blockInitialization;
            if (!blockCleanup)
            {
                ReleaseCleanup.SetResult();
            }
        }

        public TestConnectionContext TransportContext { get; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override IMessageCenter MessageCenter => null!;

        protected override async Task RunInternal()
        {
            Started.SetResult();
            try
            {
                if (_blockInitialization)
                {
                    var read = await Context.Transport.Input.ReadAsync();
                    Context.Transport.Input.AdvanceTo(read.Buffer.Start);
                    throw new ConnectionAbortedException("Initialization read terminated");
                }

                await base.RunInternal();
            }
            finally
            {
                _manager.OnConnectionTerminated(_address, this, exception: null);
                CleanupStarted.SetResult();
                await ReleaseCleanup.Task;
            }
        }

        protected override bool PrepareMessageForSend(Message message) => true;
        protected override void RetryMessage(Message message, Exception? exception = null) { }
        protected override void OnReceivedMessage(Message message) { }
        protected override void RecordMessageReceive(Message message, int numTotalBytes, int headerBytes) { }
        protected override void RecordMessageSend(Message message, int numTotalBytes, int headerBytes) { }
        protected override void OnSendMessageFailure(Message message, string error) { }
    }

    private sealed class TestConnectionContext : DefaultConnectionContext
    {
        private readonly Pipe _incoming = new();
        private readonly Pipe _outgoing = new();
        private int _disposeCount;

        public TestConnectionContext(bool blockDisposal) : base(Guid.NewGuid().ToString())
        {
            Transport = new DuplexPipe(_incoming.Reader, _outgoing.Writer);
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 12344);
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 12345);
            if (!blockDisposal)
            {
                ReleaseDisposal.SetResult();
            }
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Disposing.TrySetResult();
            await ReleaseDisposal.Task;
            await _incoming.Writer.CompleteAsync();
            await _outgoing.Reader.CompleteAsync();
            await base.DisposeAsync();
        }

        private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
        {
            public PipeReader Input { get; } = input;
            public PipeWriter Output { get; } = output;
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));

        public Task<Connection> GetConnection(ConnectionManager manager, SiloAddress address)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return manager.GetConnection(address).AsTask();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public void RunNext()
        {
            Assert.True(_callbacks.TryDequeue(out var callback));
            callback.Callback(callback.State);
        }
    }

    private sealed class PausedShutdownLogger : ILogger<ConnectionManager>, IDisposable
    {
        public TaskCompletionSource ShutdownEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseShutdown { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception) == "Shutting down connections")
            {
                ShutdownEntered.TrySetResult();
                Assert.True(ReleaseShutdown.Wait(TestTimeout), "Timed out releasing the shutdown boundary.");
            }
        }

        public void Dispose() => ReleaseShutdown.Dispose();
    }
}
