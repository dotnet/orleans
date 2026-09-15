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
using NSubstitute;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Networking.Shared;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
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
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => acquisition.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Equal("Shutting down", error.Message);
        await closing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

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
        await connection.TransportContext.Disposing.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.False(acquisition.IsCompleted);
        Assert.False(closing.IsCompleted);
        Assert.True(rig.ShutdownSource.Token.IsCancellationRequested);
        Assert.False(connection.Started.Task.IsCompleted);
        Assert.Equal(0, rig.Manager.ConnectionCount);

        connection.TransportContext.ReleaseDisposal.TrySetResult();
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => acquisition.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        await closing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, connection.TransportContext.DisposeCount);
        Assert.False(connection.Started.Task.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => rig.ShutdownSource.Token);
    }

    [Fact]
    public async Task Close_DrainsPublicationFromPreviouslyAdmittedSiloHandshake()
    {
        using var logger = new PausedShutdownLogger();
        await using var rig = new TestRig(logger);
        var acquisition = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var closing = Task.Run(() => rig.Manager.Close(CancellationToken.None), TestContext.Current.CancellationToken);
        try
        {
            await logger.ShutdownEntered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            var rejectedAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12346), 1);
            await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Manager.GetConnection(rejectedAddress).AsTask());

            var (connection, context) = rig.CreateSiloConnection();
            await using var peer = new DefaultConnectionContext { Transport = context.PeerTransport };
            attempt.Completion.SetResult(connection);
            await rig.PreambleHelper.Write(peer, new ConnectionPreamble
            {
                NodeIdentity = Constants.SiloDirectConnectionId,
                NetworkProtocolVersion = NetworkProtocolVersion.Version1,
                SiloAddress = rig.Address,
                ClusterId = "test-cluster"
            });
            var localPreamble = await rig.PreambleHelper.Read(peer).AsTask().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(connection.LocalSiloAddress, localPreamble.SiloAddress);
            Assert.Same(connection, await acquisition.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
            Assert.Equal(rig.Address, connection.RemoteSiloAddress);
            Assert.True(connection.Initialized.IsCompletedSuccessfully);
            Assert.Equal(1, rig.Manager.ConnectionCount);
            Assert.False(closing.IsCompleted);
            Assert.False(rig.ShutdownSource.Token.IsCancellationRequested);
        }
        finally
        {
            logger.ReleaseShutdown.Set();
        }

        await closing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
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
        await connection.Started.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var failure = new ConnectionAbortedException("Initialization failed in the test");
        var closing = shutdown ? rig.Manager.Close(CancellationToken.None) : connection.CloseAsync(failure);

        await connection.CleanupStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(acquisition.IsCompleted);
        Assert.Equal(shutdown, rig.ShutdownSource.Token.IsCancellationRequested);
        if (shutdown)
        {
            Assert.False(closing.IsCompleted);
            Assert.False(rig.Manager.Closed.IsCompleted);
        }

        connection.ReleaseCleanup.TrySetResult();
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => acquisition.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        if (shutdown)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        }
        else
        {
            Assert.Same(failure, error.InnerException);
        }

        await closing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
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
        Assert.Same(connection, await acquisition.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        using var hostCancellation = new CancellationTokenSource();

        var closing = rig.Manager.Close(hostCancellation.Token);
        await connection.CleanupStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Manager.ConnectionCount);
        Assert.False(closing.IsCompleted);
        hostCancellation.Cancel();
        await closing.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.True(rig.Manager.Closed.IsCompletedSuccessfully);
        Assert.True(rig.ShutdownSource.Token.IsCancellationRequested);
        Assert.False(connection.ReleaseCleanup.Task.IsCompleted);

        connection.ReleaseCleanup.TrySetResult();
        await rig.Manager.Close(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
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
        Assert.Same(connection, await first.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        Assert.Same(connection, await second.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        var reused = rig.Manager.GetConnection(rig.Address);
        Assert.True(reused.IsCompletedSuccessfully);
        Assert.Same(connection, await reused);
        Assert.True(rig.Manager.TryGetConnection(rig.Address, out var existing));
        Assert.Same(connection, existing);
        Assert.Equal(1, rig.Factory.AttemptCount);

        await rig.Manager.CloseAsync(rig.Address).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.False(rig.Manager.TryGetConnection(rig.Address, out _));
        Assert.False(rig.Manager.Closed.IsCompleted);
        Assert.False(rig.ShutdownSource.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task OnConnected_RejectsInboundPublicationAfterClose()
    {
        await using var rig = new TestRig();
        await rig.Manager.Close(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
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

        await connection.CloseAsync(exception: null).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await connection.Run().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.False(connection.Started.Task.IsCompleted);
        Assert.False(connection.IsValid);
        Assert.True(connection.Initialized.IsFaulted);
        Assert.Equal(1, connection.TransportContext.AbortCount);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
    }

    [Fact]
    public async Task InitializationTimeout_AbortsWrappedTransportBeforeMiddlewareDisposal()
    {
        var middleware = new WrappedTransportMiddleware();
        await using var rig = new TestRig(transportMiddleware: middleware, openConnectionTimeout: TimeSpan.FromSeconds(5));
        var acquisition = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var connection = rig.CreateConnection(blockInitialization: true);
        attempt.Completion.SetResult(connection);
        await connection.Started.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await connection.TransportContext.Aborted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await middleware.Unwinding.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.True(attempt.CancellationToken.IsCancellationRequested);
        Assert.False(rig.ShutdownSource.Token.IsCancellationRequested);
        Assert.False(acquisition.IsCompleted);
        Assert.False(connection.TransportContext.Disposing.Task.IsCompleted);
        Assert.NotSame(connection.TransportContext.OriginalTransport, connection.TransportContext.Transport);

        middleware.ReleaseCleanup.TrySetResult();
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => acquisition.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("timed out after", error.Message);
        Assert.Null(error.InnerException);
        Assert.Same(connection.TransportContext.OriginalTransport, connection.TransportContext.DisposedTransport);
        Assert.Equal(1, connection.TransportContext.AbortCount);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
        Assert.Equal(0, rig.Manager.ConnectionCount);
    }

    [Fact]
    public async Task Run_AbortsTransportAfterSynchronousMiddlewareFailure()
    {
        await using var rig = new TestRig();
        var failure = new InvalidOperationException("Synchronous middleware failure");
        var connection = rig.CreateConnection(middleware: _ => throw failure);

        await connection.Run().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Initialized);

        Assert.Same(failure, error);
        Assert.False(connection.Started.Task.IsCompleted);
        Assert.Equal(1, connection.TransportContext.AbortCount);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
    }

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly ConcurrentBag<(Connection Connection, TestConnectionContext Context)> _connections = [];
        private readonly SharedMemoryPool _memoryPool = new();
        private readonly ConnectionCommon _shared;
        private readonly ConnectionDelegate _middleware;
        private readonly ConnectionOptions _options;
        private readonly WrappedTransportMiddleware? _transportMiddleware;

        public TestRig(
            ILogger<ConnectionManager>? logger = null,
            WrappedTransportMiddleware? transportMiddleware = null,
            TimeSpan? openConnectionTimeout = null)
        {
            _transportMiddleware = transportMiddleware;
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
            services.AddSingleton<ConnectionPreambleHelper>();
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
            if (transportMiddleware is not null)
            {
                builder.Use(next => context => transportMiddleware.Invoke(context, next));
            }

            Connection.ConfigureBuilder(builder);
            _middleware = builder.Build();
            _options = new ConnectionOptions
            {
                OpenConnectionTimeout = openConnectionTimeout ?? System.Threading.Timeout.InfiniteTimeSpan,
                ConnectionRetryDelay = TimeSpan.FromMinutes(1)
            };
            var options = Options.Create(_options);
            Factory = new TestConnectionFactory(_services, options);
            Manager = new ConnectionManager(options, Factory, logger ?? NullLogger<ConnectionManager>.Instance);
            ShutdownSource = (CancellationTokenSource)typeof(ConnectionManager)
                .GetField("shutdownCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Manager)!;
        }

        public SiloAddress Address { get; } = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12345), 1);
        public ConnectionManager Manager { get; }
        public TestConnectionFactory Factory { get; }
        public CancellationTokenSource ShutdownSource { get; }
        public ConnectionPreambleHelper PreambleHelper => _services.GetRequiredService<ConnectionPreambleHelper>();

        public TestConnection CreateConnection(
            bool blockInitialization = false,
            bool blockCleanup = false,
            bool blockDisposal = false,
            ConnectionDelegate? middleware = null)
        {
            var context = new TestConnectionContext(blockDisposal);
            var connection = new TestConnection(context, middleware ?? _middleware, _shared, Manager, Address, blockInitialization, blockCleanup);
            _connections.Add((connection, context));
            return connection;
        }

        public (SiloConnection Connection, TestConnectionContext Context) CreateSiloConnection()
        {
            var context = new TestConnectionContext(blockDisposal: false);
            var local = Substitute.For<ILocalSiloDetails>();
            local.SiloAddress.Returns(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12344), 1));
            local.ClusterId.Returns("test-cluster");
            var connection = new SiloConnection(Address, context, _middleware, null!, local, Manager, _options, _shared, null!, PreambleHelper);
            _connections.Add((connection, context));
            return (connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            _transportMiddleware?.ReleaseCleanup.TrySetResult();
            foreach (var (connection, context) in _connections)
            {
                if (connection is TestConnection testConnection)
                {
                    testConnection.ReleaseCleanup.TrySetResult();
                }

                context.ReleaseDisposal.TrySetResult();
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
        private int _abortCount;

        public TestConnectionContext(bool blockDisposal) : base(Guid.NewGuid().ToString())
        {
            Transport = new DuplexPipe(_incoming.Reader, _outgoing.Writer);
            OriginalTransport = Transport;
            PeerTransport = new DuplexPipe(_outgoing.Reader, _incoming.Writer);
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 12344);
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 12345);
            if (!blockDisposal)
            {
                ReleaseDisposal.SetResult();
            }
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int AbortCount => Volatile.Read(ref _abortCount);
        public IDuplexPipe OriginalTransport { get; }
        public IDuplexPipe PeerTransport { get; }
        public IDuplexPipe? DisposedTransport { get; private set; }
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Abort(ConnectionAbortedException abortReason)
        {
            Interlocked.Increment(ref _abortCount);
            base.Abort(abortReason);
            Aborted.TrySetResult();
        }

        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            DisposedTransport = Transport;
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

    private sealed class WrappedTransportMiddleware
    {
        public TaskCompletionSource Unwinding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Invoke(ConnectionContext context, ConnectionDelegate next)
        {
            var original = context.Transport;
            var wrapped = new StreamTransport(original);
            context.Transport = wrapped;
            try
            {
                await next(context);
            }
            finally
            {
                Unwinding.TrySetResult();
                await ReleaseCleanup.Task;
                try
                {
                    await wrapped.Input.CompleteAsync();
                    await wrapped.Output.CompleteAsync();
                }
                finally
                {
                    context.Transport = original;
                }
            }
        }

        private sealed class StreamTransport(IDuplexPipe underlying) : IDuplexPipe
        {
            public PipeReader Input { get; } = PipeReader.Create(underlying.Input.AsStream(leaveOpen: true), new StreamPipeReaderOptions(leaveOpen: true));
            public PipeWriter Output { get; } = PipeWriter.Create(underlying.Output.AsStream(leaveOpen: true), new StreamPipeWriterOptions(leaveOpen: true));
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
