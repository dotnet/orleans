using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Connections.Transport;
using Orleans.Metadata;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Invocation;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class MessageCenterTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SendMessage_KnownDeadGrainTarget_ReaddressesBeforeTransport(
        bool oneWay, bool hasExistingConnection)
    {
        await using var fixture = await RoutingFixture.CreateAsync();
        if (hasExistingConnection)
        {
            fixture.Manager.OnConnected(fixture.OldSilo, fixture.OldConnection);
        }

        fixture.MarkOldSiloDead();
        using var message = fixture.CreateMessage(oneWay ? Message.Directions.OneWay : Message.Directions.Request);

        fixture.Target.SendMessage(message);

        Assert.Same(message, await fixture.ReplacementConnection.NextMessage());
        Assert.Equal(fixture.ReplacementSilo, message.TargetSilo);
        Assert.Equal(1, message.ForwardCount);
        Assert.Equal(0, message.RetryCount);
        Assert.Same(fixture.Body, message.BodyObject);
        Assert.False(fixture.OldConnection.HasMessages);
        Assert.False(fixture.Factory.HasAttempts);
        fixture.Directory.Received(1).InvalidateCacheEntry(
            Arg.Is<GrainAddress>(address => address.GrainId == message.TargetGrain && address.SiloAddress == fixture.OldSilo));
        var update = Assert.Single(message.CacheInvalidationHeader!);
        Assert.Equal(fixture.OldSilo, update.InvalidGrainAddress.SiloAddress);
        Assert.Null(update.ValidGrainAddress);
        Assert.False(fixture.Completion.Task.IsCompleted);

        if (!oneWay)
        {
            fixture.CompleteRequest(message);
            Assert.Null((await fixture.Completion.Task.WaitAsync(TestContext.Current.CancellationToken)).Exception);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendMessage_TargetDiesDuringConnectionAcquisition_ReaddressesUnsentRequest(bool connectionSucceeds)
    {
        await using var fixture = await RoutingFixture.CreateAsync();
        using var message = fixture.CreateMessage(Message.Directions.Request);
        fixture.Target.SendMessage(message);
        var attempt = await fixture.Factory.NextAttempt();
        Assert.Equal(fixture.OldSilo, attempt.Address);
        Assert.False(fixture.Completion.Task.IsCompleted);

        fixture.MarkOldSiloDead();
        if (connectionSucceeds)
        {
            attempt.Completion.SetResult(fixture.OldConnection);
        }
        else
        {
            attempt.Completion.SetException(new InvalidOperationException("The retiring endpoint closed."));
        }

        Assert.Same(message, await fixture.ReplacementConnection.NextMessage());
        Assert.Equal(fixture.ReplacementSilo, message.TargetSilo);
        Assert.Equal(1, message.ForwardCount);
        Assert.False(fixture.OldConnection.HasMessages);
        fixture.CompleteRequest(message);
        Assert.Null((await fixture.Completion.Task.WaitAsync(TestContext.Current.CancellationToken)).Exception);
    }

    [Fact]
    public async Task SendMessage_KnownDeadGrainTarget_RespectsForwardLimit()
    {
        await using var fixture = await RoutingFixture.CreateAsync();
        fixture.MarkOldSiloDead();
        using var message = fixture.CreateMessage(Message.Directions.Request);
        message.ForwardCount = fixture.Options.MaxForwardCount;

        fixture.Target.SendMessage(message);

        var response = await fixture.Completion.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.IsType<SiloUnavailableException>(response.Exception);
        Assert.Equal(fixture.Options.MaxForwardCount, message.ForwardCount);
        Assert.False(fixture.ReplacementConnection.HasMessages);
        Assert.False(fixture.Factory.HasAttempts);
        await fixture.Directory.DidNotReceive().LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SendMessage_RepeatedDeadDirectoryResult_StopsAtForwardLimit()
    {
        await using var fixture = await RoutingFixture.CreateAsync();
        fixture.MarkOldSiloDead();
        using var message = fixture.CreateMessage(Message.Directions.Request);
        fixture.Directory.LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>()).Returns(call =>
            Task.FromResult(new AddressAndTag(
                new GrainAddress { GrainId = call.Arg<GrainId>(), SiloAddress = fixture.OldSilo }, 0)));

        fixture.Target.SendMessage(message);

        Assert.IsType<SiloUnavailableException>(
            (await fixture.Completion.Task.WaitAsync(TestContext.Current.CancellationToken)).Exception);
        Assert.Equal(fixture.Options.MaxForwardCount, message.ForwardCount);
        Assert.False(fixture.ReplacementConnection.HasMessages);
        Assert.False(fixture.Factory.HasAttempts);
        await fixture.Directory.Received(fixture.Options.MaxForwardCount).LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SendMessage_TransportRetryToKnownDeadSilo_PreservesFailure()
    {
        await using var fixture = await RoutingFixture.CreateAsync();
        fixture.MarkOldSiloDead();
        using var message = fixture.CreateMessage(Message.Directions.Request);
        message.RetryCount = 1;

        fixture.Target.SendMessage(message);

        Assert.IsType<SiloUnavailableException>(
            (await fixture.Completion.Task.WaitAsync(TestContext.Current.CancellationToken)).Exception);
        Assert.Equal(0, message.ForwardCount);
        Assert.False(fixture.ReplacementConnection.HasMessages);
        await fixture.Directory.DidNotReceive().LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SendMessage_KnownDeadSystemTarget_PreservesEndpointFailure()
    {
        await using var fixture = await RoutingFixture.CreateAsync();
        fixture.MarkOldSiloDead();
        using var message = fixture.CreateMessage(Message.Directions.Request);
        message.TargetGrain = SystemTargetGrainId.Create(Constants.CatalogType, fixture.OldSilo).GrainId;
        message.IsSystemMessage = true;

        fixture.Target.SendMessage(message);

        Assert.IsType<SiloUnavailableException>(
            (await fixture.Completion.Task.WaitAsync(TestContext.Current.CancellationToken)).Exception);
        Assert.Equal(fixture.OldSilo, message.TargetSilo);
        Assert.Equal(0, message.ForwardCount);
        Assert.False(fixture.ReplacementConnection.HasMessages);
        fixture.Directory.DidNotReceive().InvalidateCacheEntry(Arg.Any<GrainAddress>());
    }

    [Fact]
    public async Task SendMessage_LiveGrainTarget_PreservesExistingConnection()
    {
        await using var fixture = await RoutingFixture.CreateAsync();
        fixture.Manager.OnConnected(fixture.OldSilo, fixture.OldConnection);
        using var message = fixture.CreateMessage(Message.Directions.Request);

        fixture.Target.SendMessage(message);

        Assert.Same(message, await fixture.OldConnection.NextMessage());
        Assert.Equal(fixture.OldSilo, message.TargetSilo);
        Assert.Equal(0, message.ForwardCount);
        Assert.Null(message.CacheInvalidationHeader);
        Assert.False(fixture.ReplacementConnection.HasMessages);
        await fixture.Directory.DidNotReceive().LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>());
        fixture.CompleteRequest(message);
        Assert.Null((await fixture.Completion.Task.WaitAsync(TestContext.Current.CancellationToken)).Exception);
    }

    private sealed class RoutingFixture : IAsyncDisposable
    {
        private readonly InProcessTestCluster _cluster;
        private readonly ServiceProvider _routingServices;
        private readonly PlacementService _placement;
        private readonly ISiloStatusOracle _oracle = Substitute.For<ISiloStatusOracle>();
        private readonly InsideRuntimeClient _runtime;
        private readonly MessageFactory _messageFactory;
        private readonly HostedClient _hostedClient;
        private readonly ConcurrentDictionary<(GrainId, CorrelationId), CallbackData> _callbacks;

        private RoutingFixture(InProcessTestCluster cluster)
        {
            _cluster = cluster;
            var services = cluster.Silos[0].ServiceProvider;
            _runtime = services.GetRequiredService<InsideRuntimeClient>();
            _messageFactory = services.GetRequiredService<MessageFactory>();
            _hostedClient = services.GetRequiredService<HostedClient>();
            _callbacks = (ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>)typeof(InsideRuntimeClient)
                .GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_runtime)!;
            var local = Substitute.For<ILocalSiloDetails>();
            local.SiloAddress.Returns(cluster.Silos[0].SiloAddress);
            var routingServices = new ServiceCollection();
            routingServices.AddLogging();
            routingServices.AddSingleton(TimeProvider.System);
            routingServices.AddOptions<SiloMessagingOptions>().Configure(options => options.PlacementMaxRetries = 0);
            routingServices.AddOrleansRuntimeResiliencePolicies();
            _routingServices = routingServices.BuildServiceProvider();
            var resolver = new GrainDirectoryResolver(
                _routingServices, new GrainPropertiesResolver(services.GetRequiredService<IClusterManifestProvider>()), []);
            var locator = new GrainLocator(
                new GrainLocatorResolver(_routingServices, resolver, null!, new DhtGrainLocator(Directory, null!)), null!);
            Directory.LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>()).Returns(call =>
                Task.FromResult(new AddressAndTag(
                    new GrainAddress { GrainId = call.Arg<GrainId>(), SiloAddress = ReplacementSilo }, 0)));
            _placement = new PlacementService(
                _routingServices.GetRequiredService<IOptionsMonitor<SiloMessagingOptions>>(), local, _oracle,
                NullLogger<PlacementService>.Instance, locator, null!, null!, null!, null!, null!, null!,
                _routingServices.GetRequiredService<Polly.Registry.ResiliencePipelineProvider<string>>());
            Manager = new ConnectionManager(
                Microsoft.Extensions.Options.Options.Create(new ConnectionOptions()), Factory, NullLogger<ConnectionManager>.Instance);
            var shared = services.GetRequiredService<ConnectionCommon>();
            OldConnection = new CapturingConnection(shared);
            ReplacementConnection = new CapturingConnection(shared);
            Manager.OnConnected(ReplacementSilo, ReplacementConnection);
            Target = new MessageCenter(
                local, _messageFactory, services.GetRequiredService<Catalog>(), null!, NullLogger<MessageCenter>.Instance,
                _oracle, Manager, services.GetRequiredService<RuntimeMessagingTrace>(),
                services.GetRequiredService<MessagingInstruments>(), services.GetRequiredService<MessagingProcessingInstruments>(),
                Microsoft.Extensions.Options.Options.Create(Options), _placement, locator, new NoOpMessageStatisticsSink());
            Target.SetHostedClient(_hostedClient);
        }

        public static async Task<RoutingFixture> CreateAsync()
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.ConfigureHost(host => TestDefaultConfiguration.ConfigureHostConfiguration(host.Configuration));
            var cluster = builder.Build();
            await cluster.DeployAsync(TestContext.Current.CancellationToken);
            return new RoutingFixture(cluster);
        }

        public SiloAddress OldSilo { get; } = SiloAddress.New(IPAddress.Loopback, 11112, 1);
        public SiloAddress ReplacementSilo { get; } = SiloAddress.New(IPAddress.Loopback, 11113, 1);
        public ILocalGrainDirectory Directory { get; } = Substitute.For<ILocalGrainDirectory>();
        public SiloMessagingOptions Options { get; } = new();
        public PendingConnectionFactory Factory { get; } = new();
        public ConnectionManager Manager { get; }
        public CapturingConnection OldConnection { get; }
        public CapturingConnection ReplacementConnection { get; }
        public MessageCenter Target { get; }
        public object Body { get; } = new();
        public TaskCompletionSource<Response> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkOldSiloDead() => _oracle.IsDeadSilo(OldSilo).Returns(true);

        public Message CreateMessage(Message.Directions direction)
        {
            var message = new Message
            {
                Direction = direction,
                Id = new CorrelationId(1),
                SendingGrain = _hostedClient.GrainId,
                SendingSilo = _cluster.Silos[0].SiloAddress,
                TargetGrain = GrainId.Create("test", "shutdown-routing"),
                TargetSilo = OldSilo,
                BodyObject = Body,
            };
            if (direction == Message.Directions.Request)
            {
                var shared = new SharedCallbackData(
                    msg => _callbacks.TryRemove((msg.SendingGrain, msg.Id), out _),
                    NullLogger<CallbackData>.Instance, TimeProvider.System, TimeSpan.FromMinutes(1),
                    cancelOnTimeout: false, waitForCancellationAcknowledgement: false, cancellationManager: null);
                Assert.True(_callbacks.TryAdd(
                    (message.SendingGrain, message.Id),
                    new CallbackData(shared, new CompletionSource(Completion), message,
                        _cluster.Silos[0].ServiceProvider.GetRequiredService<ApplicationRequestInstruments>())));
            }

            return message;
        }

        public void CompleteRequest(Message message)
        {
            var response = _messageFactory.CreateResponseMessage(message);
            response.BodyObject = Response.Completed;
            Target.ReceiveMessage(response);
        }

        public async ValueTask DisposeAsync()
        {
            var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            ((ILifecycleParticipant<ISiloLifecycle>)_placement).Participate(lifecycle);
            await lifecycle.OnStart(CancellationToken.None);
            await lifecycle.OnStop(CancellationToken.None);
            OldConnection.OnReadCompleted(new ConnectionClosedException());
            ReplacementConnection.OnReadCompleted(new ConnectionClosedException());
            await OldConnection.CloseAsync(null);
            await Manager.Close(CancellationToken.None);
            await Target.DisposeAsync();
            await _routingServices.DisposeAsync();
            await _cluster.DisposeAsync();
        }
    }

    private sealed class CompletionSource(TaskCompletionSource<Response> completion) : IResponseCompletionSource
    {
        public void Complete(Response response) => completion.TrySetResult(response);
        public void Complete() => Complete(Response.Completed);
    }

    private sealed class PendingConnectionFactory : ConnectionFactory
    {
        private readonly Channel<(SiloAddress Address, TaskCompletionSource<Connection> Completion)> _attempts =
            Channel.CreateUnbounded<(SiloAddress, TaskCompletionSource<Connection>)>();

        public PendingConnectionFactory() : base(null!, []) { }
        public bool HasAttempts => _attempts.Reader.TryPeek(out _);
        public override ValueTask<Connection> ConnectAsync(SiloAddress address, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<Connection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _attempts.Writer.TryWrite((address, completion));
            return new(completion.Task.WaitAsync(cancellationToken));
        }

        public Task<(SiloAddress Address, TaskCompletionSource<Connection> Completion)> NextAttempt() =>
            _attempts.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        protected override Connection CreateConnection(SiloAddress address, MessageTransport transport) => throw new NotSupportedException();
        protected override EndPoint GetEndPoint(SiloAddress address) => address.Endpoint;
    }

    private sealed class CapturingConnection : Connection
    {
        private readonly Channel<Message> _messages = Channel.CreateUnbounded<Message>();

        public CapturingConnection(ConnectionCommon shared) : base(CreateTransport(), shared) { }
        public bool HasMessages => _messages.Reader.TryPeek(out _);
        public Task<Message> NextMessage() =>
            _messages.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public override void Send(Message message) => _messages.Writer.TryWrite(message);
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;
        protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(10);
        protected override IMessageCenter MessageCenter => throw new NotSupportedException();
        protected override bool PrepareMessageForSend(Message message) => throw new NotSupportedException();
        protected override void RetryMessage(Message message, Exception? exception = null) => throw new NotSupportedException();
        protected internal override void OnReceivedMessage(Message message) => throw new NotSupportedException();
        protected internal override void RecordMessageReceive(Message message, int totalBytes, int headerBytes) { }
        protected internal override void RecordMessageSend(Message message, int totalBytes, int headerBytes) { }

        private static MessageTransport CreateTransport()
        {
            var transport = Substitute.For<MessageTransport>();
            transport.EnqueueRead(Arg.Any<ReadRequest>()).Returns(true);
            return transport;
        }
    }
}
