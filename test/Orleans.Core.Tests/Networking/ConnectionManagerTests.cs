using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Connections;
using Orleans.Connections.Transport;
using Orleans.Metadata;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.TestingHost.InMemoryTransport;
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

    [Theory]
    [InlineData((int)Message.Directions.Request)]
    [InlineData((int)Message.Directions.Response)]
    [InlineData((int)Message.Directions.OneWay)]
    public async Task SiloConnection_Shutdown_InvalidatesRequestsAndReleasesBufferedBodies(int direction)
    {
        await using var rig = new TestRig();
        var responses = new List<Message>();
        var sender = rig.CreateConnection();
        sender.SendObserver = responses.Add;
        rig.Manager.OnConnected(rig.Address, sender);
        var (connection, _) = rig.CreateSiloConnection(blockApplicationMessages: true);
        using var body = new ArcBufferWriter();
        body.Write([1, 2, 3]);
        var readRequest = rig.CreateReadRequest();
        readRequest.Body = body.ConsumeSlice(body.Length);
        using var message = new Message
        {
            Direction = (Message.Directions)direction,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = GrainId.Create("test", "target"),
            SendingSilo = rig.Address,
            TargetSilo = connection.LocalSiloAddress
        };
        message.SetMessageReadRequest(readRequest);

        try
        {
            connection.OnReceivedMessage(message);

            Assert.Null(message._bodyObject);
            Assert.Equal(0, readRequest.Body.Length);
            if (message.Direction is Message.Directions.Request)
            {
                var response = Assert.Single(responses);
                Assert.Equal(Message.Directions.Response, response.Direction);
                Assert.Equal(Message.ResponseTypes.Rejection, response.Result);
                Assert.Equal(message.Id, response.Id);
                Assert.Equal(message.SendingGrain, response.TargetGrain);
                Assert.Equal(rig.Address, response.TargetSilo);
                var rejection = Assert.IsType<RejectionResponse>(response.BodyObject);
                Assert.Equal(Message.RejectionTypes.Transient, rejection.RejectionType);
                Assert.Contains("Silo stopping", rejection.RejectionInfo);
                var invalidation = Assert.Single(response.CacheInvalidationHeader!);
                Assert.Equal(message.TargetGrain, invalidation.GrainId);
                Assert.Equal(connection.LocalSiloAddress, invalidation.InvalidSiloAddress);
                Assert.Null(invalidation.ValidGrainAddress);
                rig.GrainDirectory.Received(1).InvalidateCacheEntry(
                    Arg.Is<GrainAddress>(address => address.GrainId == message.TargetGrain
                        && address.SiloAddress == connection.LocalSiloAddress));
            }
            else
            {
                Assert.Empty(responses);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }

            await sender.CloseAsync(exception: null).WaitAsync(TestTimeout, CancellationToken.None);
            rig.Manager.OnConnectionTerminated(rig.Address, sender, exception: null);
        }
    }

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
            var peer = context.PeerTransport;
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
    public async Task RunAsync_ClosesQueuedConnectionBeforeStarting()
    {
        await using var rig = new TestRig();
        var connection = rig.CreateConnection();

        await connection.CloseAsync(exception: null).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await connection.RunAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.False(connection.Started.Task.IsCompleted);
        Assert.False(connection.IsValid);
        Assert.True(connection.Initialized.IsFaulted);
        Assert.Equal(1, connection.TransportContext.CloseCount);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
    }

    [Fact]
    public async Task InitializationTimeout_ClosesWrappedTransportBeforeDisposal()
    {
        var middleware = new WrappedTransportMiddleware();
        await using var rig = new TestRig(transportMiddleware: middleware, openConnectionTimeout: TimeSpan.FromSeconds(5));
        var acquisition = rig.Manager.GetConnection(rig.Address).AsTask();
        var attempt = await rig.Factory.NextAttempt();
        var connection = rig.CreateConnection(blockInitialization: true);
        attempt.Completion.SetResult(connection);
        await connection.Started.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await connection.TransportContext.Closing.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        await middleware.Unwinding.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.True(attempt.CancellationToken.IsCancellationRequested);
        Assert.False(rig.ShutdownSource.Token.IsCancellationRequested);
        Assert.False(acquisition.IsCompleted);
        Assert.False(connection.TransportContext.Disposing.Task.IsCompleted);
        Assert.NotSame(connection.TransportContext, connection.MessageTransport);

        middleware.ReleaseCleanup.TrySetResult();
        var error = await Assert.ThrowsAsync<ConnectionFailedException>(() => acquisition.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));

        Assert.Contains("timed out after", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, connection.TransportContext.CloseCount);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
        Assert.Equal(0, rig.Manager.ConnectionCount);
    }

    [Fact]
    public async Task RunAsync_ClosesTransportAfterSynchronousInitializationFailure()
    {
        await using var rig = new TestRig();
        var failure = new InvalidOperationException("Synchronous initialization failure");
        var connection = rig.CreateConnection(initialize: _ => throw failure);

        await connection.RunAsync().WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Initialized);

        Assert.Same(failure, error);
        Assert.False(connection.Started.Task.IsCompleted);
        Assert.Equal(1, connection.TransportContext.CloseCount);
        Assert.Equal(1, connection.TransportContext.DisposeCount);
    }

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly ConcurrentBag<(Connection Connection, TestMessageTransport Context)> _connections = [];
        private readonly List<MessageCenter> _messageCenters = [];
        private readonly ConnectionCommon _shared;
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
            services.AddSingleton<NetworkingInstruments>();
            services.AddSingleton<MessagingProcessingInstruments>();
            services.AddSingleton<MessagingOptions>(new ClientMessagingOptions());
            services.AddTransient<MessageSerializer>();
            services.AddSingleton<ConnectionPreambleHelper>();
            services.AddSingleton(sp => new MessagingTrace(
                NullLoggerFactory.Instance,
                sp.GetRequiredService<MessagingInstruments>(),
                sp.GetRequiredService<MessagingProcessingInstruments>()));
            services.AddSingleton(new ConnectionTrace(NullLoggerFactory.Instance));
            services.AddSingleton(sp => new MessageFactory(
                sp.GetRequiredService<DeepCopier>(),
                NullLogger<MessageFactory>.Instance,
                sp.GetRequiredService<MessagingTrace>()));
            services.AddSingleton<MessageHandlerShared>(sp => new(
                sp.GetRequiredService<MessagingTrace>(),
                sp.GetRequiredService<ConnectionTrace>(),
                () => sp.GetRequiredService<MessageSerializer>(),
                sp.GetRequiredService<MessageFactory>(),
                Substitute.For<IMessageCenter>(),
                sp.GetRequiredService<MessagingInstruments>()));
            _services = services.BuildServiceProvider();
            var instruments = _services.GetRequiredService<MessagingInstruments>();
            _shared = new ConnectionCommon(
                _services,
                _services.GetRequiredService<MessageFactory>(),
                _services.GetRequiredService<MessagingTrace>(),
                _services.GetRequiredService<ConnectionTrace>(),
                instruments,
                _services.GetRequiredService<NetworkingInstruments>(),
                new NoOpMessageStatisticsSink());
            _options = new ConnectionOptions
            {
                OpenConnectionTimeout = openConnectionTimeout ?? System.Threading.Timeout.InfiniteTimeSpan,
                ConnectionRetryDelay = TimeSpan.FromMinutes(1)
            };
            var options = Options.Create(_options);
            Factory = new TestConnectionFactory();
            Manager = new ConnectionManager(options, Factory, logger ?? NullLogger<ConnectionManager>.Instance);
            ShutdownSource = (CancellationTokenSource)typeof(ConnectionManager)
                .GetField("shutdownCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Manager)!;
        }

        public SiloAddress Address { get; } = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12345), 1);
        public ConnectionManager Manager { get; }
        public ILocalGrainDirectory GrainDirectory { get; } = Substitute.For<ILocalGrainDirectory>();
        public TestConnectionFactory Factory { get; }
        public CancellationTokenSource ShutdownSource { get; }
        public ConnectionPreambleHelper PreambleHelper => _services.GetRequiredService<ConnectionPreambleHelper>();

        public TestConnection CreateConnection(
            bool blockInitialization = false,
            bool blockCleanup = false,
            bool blockDisposal = false,
            Func<MessageTransport, Task>? initialize = null)
        {
            var context = new TestMessageTransport(blockDisposal);
            var transport = _transportMiddleware?.Wrap(context) ?? (MessageTransport)context;
            var connection = new TestConnection(context, transport, _shared, Manager, Address, PreambleHelper, blockInitialization, blockCleanup, initialize);
            _connections.Add((connection, context));
            return connection;
        }

        public MessageReadRequest CreateReadRequest() => _shared.MessageHandlerShared.GetReceiveMessageHandler();

        public (SiloConnection Connection, TestMessageTransport Context) CreateSiloConnection(bool blockApplicationMessages = false)
        {
            var context = new TestMessageTransport(blockDisposal: false);
            var local = Substitute.For<ILocalSiloDetails>();
            local.SiloAddress.Returns(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 12344), 1));
            local.ClusterId.Returns("test-cluster");
            MessageCenter? messageCenter = null;
            if (blockApplicationMessages)
            {
                var directoryResolver = new GrainDirectoryResolver(
                    _services,
                    new GrainPropertiesResolver(Substitute.For<IClusterManifestProvider>()),
                    []);
                var locatorResolver = new GrainLocatorResolver(
                    _services,
                    directoryResolver,
                    null!,
                    new DhtGrainLocator(GrainDirectory, null!));
                messageCenter = new MessageCenter(
                    local,
                    _shared.MessageFactory,
                    null!,
                    null!,
                    NullLogger<MessageCenter>.Instance,
                    Substitute.For<ISiloStatusOracle>(),
                    Manager,
                    new RuntimeMessagingTrace(
                        NullLoggerFactory.Instance,
                        _services.GetRequiredService<MessagingInstruments>(),
                        _services.GetRequiredService<MessagingProcessingInstruments>()),
                    _services.GetRequiredService<MessagingInstruments>(),
                    _services.GetRequiredService<MessagingProcessingInstruments>(),
                    Options.Create(new SiloMessagingOptions()),
                    null!,
                    new GrainLocator(locatorResolver, null!),
                    new NoOpMessageStatisticsSink());
                messageCenter.BlockApplicationMessages();
                _messageCenters.Add(messageCenter);
            }

            var connection = new SiloConnection(Address, context, messageCenter!, local, Manager, _options, _shared, null!, PreambleHelper);
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
            foreach (var messageCenter in _messageCenters)
            {
                await messageCenter.DisposeAsync();
            }

            await _services.DisposeAsync();
        }
    }

    private sealed class TestConnectionFactory() : ConnectionFactory(null!, [])
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

        protected override Connection CreateConnection(SiloAddress address, MessageTransport transport) => throw new NotSupportedException();
        protected override EndPoint GetEndPoint(SiloAddress address) => address.Endpoint;
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
        private readonly ConnectionPreambleHelper _preambleHelper;
        private readonly Func<MessageTransport, Task>? _initialize;

        public TestConnection(
            TestMessageTransport context,
            MessageTransport transport,
            ConnectionCommon shared,
            ConnectionManager manager,
            SiloAddress address,
            ConnectionPreambleHelper preambleHelper,
            bool blockInitialization,
            bool blockCleanup,
            Func<MessageTransport, Task>? initialize) : base(transport, shared)
        {
            TransportContext = context;
            _manager = manager;
            _address = address;
            _blockInitialization = blockInitialization;
            _preambleHelper = preambleHelper;
            _initialize = initialize;
            if (!blockCleanup)
            {
                ReleaseCleanup.SetResult();
            }
        }

        public TestMessageTransport TransportContext { get; }
        public MessageTransport MessageTransport => Context;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<Message>? SendObserver { get; set; }
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TestTimeout;
        protected override IMessageCenter MessageCenter => null!;

        protected override async Task RunAsyncCore()
        {
            try
            {
                if (_initialize is not null)
                {
                    await _initialize(Context);
                }

                Started.SetResult();
                if (_blockInitialization)
                {
                    await _preambleHelper.Read(Context);
                    throw new ConnectionAbortedException("Initialization read terminated");
                }

                await base.RunAsyncCore();
            }
            finally
            {
                _manager.OnConnectionTerminated(_address, this, exception: null);
                CleanupStarted.SetResult();
                await ReleaseCleanup.Task;
            }
        }

        protected override bool PrepareMessageForSend(Message message) => true;
        public override void Send(Message message)
        {
            if (SendObserver is { } observer)
            {
                observer(message);
            }
            else
            {
                base.Send(message);
            }
        }

        protected override void RetryMessage(Message message, Exception? exception = null) { }
        protected internal override void OnReceivedMessage(Message message) { }
        protected internal override void RecordMessageReceive(Message message, int numTotalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int numTotalBytes, int headerBytes) { }
    }

    private sealed class TestMessageTransport : MessageTransport
    {
        private readonly InMemoryMessageTransport _transport;
        private int _disposeCount;
        private int _closeCount;

        public TestMessageTransport(bool blockDisposal)
        {
            var incoming = new Pipe();
            var outgoing = new Pipe();
            _transport = new InMemoryMessageTransport(new DuplexPipe(incoming.Reader, outgoing.Writer), NullLogger.Instance);
            PeerTransport = new InMemoryMessageTransport(new DuplexPipe(outgoing.Reader, incoming.Writer), NullLogger.Instance);
            _transport.Start();
            PeerTransport.Start();
            Features.Set<IConnectionEndPointFeature>(new ConnectionEndPointFeature
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 12344),
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 12345)
            });
            if (!blockDisposal)
            {
                ReleaseDisposal.SetResult();
            }
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int CloseCount => Volatile.Read(ref _closeCount);
        public InMemoryMessageTransport PeerTransport { get; }
        public override CancellationToken Closed => _transport.Closed;
        public override IFeatureCollection Features => _transport.Features;
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool EnqueueRead(ReadRequest request) => _transport.EnqueueRead(request);
        public override bool EnqueueWrite(WriteRequest request) => _transport.EnqueueWrite(request);

        public override async ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _closeCount);
            Closing.TrySetResult();
            await _transport.CloseAsync(closeException, cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Disposing.TrySetResult();
            await ReleaseDisposal.Task;
            await _transport.DisposeAsync();
            await PeerTransport.DisposeAsync();
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

        public MessageTransport Wrap(MessageTransport transport) => new WrappedTransport(transport, this);

        private sealed class WrappedTransport(MessageTransport inner, WrappedTransportMiddleware middleware) : MessageTransport
        {
            public override CancellationToken Closed => inner.Closed;
            public override IFeatureCollection Features => inner.Features;
            public override bool EnqueueRead(ReadRequest request) => inner.EnqueueRead(request);
            public override bool EnqueueWrite(WriteRequest request) => inner.EnqueueWrite(request);

            public override async ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default)
            {
                await inner.CloseAsync(closeException, cancellationToken);
                middleware.Unwinding.TrySetResult();
                await middleware.ReleaseCleanup.Task;
            }

            public override ValueTask DisposeAsync() => inner.DisposeAsync();
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
