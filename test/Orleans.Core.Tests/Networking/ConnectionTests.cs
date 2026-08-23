using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Networking.Shared;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Session;
using TestExtensions;
using Xunit;

namespace UnitTests.Networking;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Networking")]
public class ConnectionTests
{
    private readonly MessageFactory _messageFactory;
    private readonly ConnectionCommon _connectionCommon;
    private readonly TestEnvironmentFixture _fixture;

    public ConnectionTests(TestEnvironmentFixture fixture)
    {
        _fixture = fixture;
        _messageFactory = fixture.Services.GetRequiredService<MessageFactory>();
        _connectionCommon = fixture.Services.GetRequiredService<ConnectionCommon>();
    }

    [Fact]
    public void SendFailure_RoutesForwardedRequestErrorToOriginalSilo()
    {
        var messageCenter = Substitute.For<IMessageCenter>();
        var connection = new TestConnection(_connectionCommon, messageCenter);
        var originalSilo = SiloAddress.New(IPAddress.Loopback, 11111, 1);
        var forwardingSilo = SiloAddress.New(IPAddress.Loopback, 22222, 2);
        var request = _messageFactory.CreateMessage(null, InvokeMethodOptions.None);
        request.SendingSilo = originalSilo;
        request.SendingGrain = GrainId.Create("sender", "1");
        request.TargetSilo = forwardingSilo;
        request.TargetGrain = GrainId.Create("target", "2");

        Assert.True(connection.HandleSendFailure(request, new InvalidOperationException("Failed to serialize")));

        messageCenter.Received(1).SendMessage(Arg.Is<Message>(response =>
            response.Direction == Message.Directions.Response
            && response.TargetSilo == originalSilo
            && response.TargetGrain == request.SendingGrain
            && response.Result == Message.ResponseTypes.Error));
        messageCenter.DidNotReceiveWithAnyArgs().DispatchLocalMessage(default!);
    }

    [Fact]
    public void SendFailure_DeliversClientRequestErrorLocally()
    {
        var messageCenter = Substitute.For<IMessageCenter>();
        var connection = new TestConnection(_connectionCommon, messageCenter);
        var request = _messageFactory.CreateMessage(null, InvokeMethodOptions.None);
        request.SendingGrain = GrainId.Create("client", "1");
        request.TargetSilo = SiloAddress.New(IPAddress.Loopback, 22222, 2);
        request.TargetGrain = GrainId.Create("target", "2");

        Assert.True(connection.HandleSendFailure(request, new InvalidOperationException("Failed to serialize")));

        messageCenter.Received(1).DispatchLocalMessage(Arg.Is<Message>(response =>
            response.Direction == Message.Directions.Response
            && response.TargetSilo == null
            && response.TargetGrain == request.SendingGrain
            && response.Result == Message.ResponseTypes.Error));
        messageCenter.DidNotReceiveWithAnyArgs().SendMessage(default!);
    }

    [Fact]
    public async Task SerializationFailure_IsNotFailedAgainWhenConnectionCloses()
    {
        var messageCenter = new TestMessageCenter();
        var context = CreateConnectionContext();
        var builder = new ConnectionBuilder(_connectionCommon.ServiceProvider);
        Connection.ConfigureBuilder(builder);
        var connection = new TestConnection(context, builder.Build(), _connectionCommon, messageCenter);
        var request = _messageFactory.CreateMessage(null, InvokeMethodOptions.None);
        request.BodyObject = new UndecodedRequestBody([], "alias");
        request.SendingSilo = SiloAddress.New(IPAddress.Loopback, 33333, 3);
        request.SendingGrain = GrainId.Create("sender", "1");
        request.TargetSilo = SiloAddress.New(IPAddress.Loopback, 44444, 4);
        request.TargetGrain = GrainId.Create("target", "2");

        var runTask = connection.Run();
        await connection.Initialized.WaitAsync(TimeSpan.FromSeconds(30));
        connection.Send(request);

        var response = await messageCenter.SentMessage.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Message.Directions.Response, response.Direction);
        Assert.Equal(Message.ResponseTypes.Error, response.Result);

        await connection.CloseAsync(null).WaitAsync(TimeSpan.FromSeconds(30));
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, connection.SendFailureCount);
    }

    [Fact]
    public async Task InvalidMessageFrame_RemainsOwnedUntilConnectionCloses()
    {
        var serializer = new MessageSerializer(
            _fixture.Services.GetRequiredService<SerializerSessionPool>(),
            _fixture.Services.GetRequiredService<SharedMemoryPool>(),
            new SiloMessagingOptions { MaxMessageBodySize = 1 });
        using var services = new ServiceCollection()
            .AddSingleton(serializer)
            .BuildServiceProvider();
        var connectionCommon = new ConnectionCommon(
            services,
            _messageFactory,
            _fixture.Services.GetRequiredService<MessagingTrace>(),
            _fixture.Services.GetRequiredService<OrleansInstruments>(),
            _fixture.Services.GetRequiredService<MessagingInstruments>(),
            _fixture.Services.GetRequiredService<ILogger<Connection>>(),
            _fixture.Services.GetRequiredService<IMessageStatisticsSink>());
        var messageCenter = new TestMessageCenter();
        var context = CreateConnectionContext();
        var builder = new ConnectionBuilder(connectionCommon.ServiceProvider);
        Connection.ConfigureBuilder(builder);
        var connection = new TestConnection(context, builder.Build(), connectionCommon, messageCenter);
        var request = _messageFactory.CreateMessage(new object[] { "too large" }, InvokeMethodOptions.None);
        request.SendingSilo = SiloAddress.New(IPAddress.Loopback, 33333, 3);
        request.SendingGrain = GrainId.Create("sender", "1");
        request.TargetSilo = SiloAddress.New(IPAddress.Loopback, 44444, 4);
        request.TargetGrain = GrainId.Create("target", "2");

        var runTask = connection.Run();
        await connection.Initialized.WaitAsync(TimeSpan.FromSeconds(30));
        connection.Send(request);

        Assert.Same(request, await connection.SendFailure.WaitAsync(TimeSpan.FromSeconds(30)));
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, connection.SendFailureCount);
        Assert.False(messageCenter.SentMessage.IsCompleted);
    }

    [Theory]
    [InlineData(NetworkProtocolVersion.Version1, NetworkProtocolVersion.Version1, NetworkProtocolVersion.Version1)]
    [InlineData(NetworkProtocolVersion.Version1, NetworkProtocolVersion.Version2, NetworkProtocolVersion.Version1)]
    [InlineData(NetworkProtocolVersion.Version2, NetworkProtocolVersion.Version1, NetworkProtocolVersion.Version1)]
    [InlineData(NetworkProtocolVersion.Version2, NetworkProtocolVersion.Version2, NetworkProtocolVersion.Version2)]
    public void ProtocolNegotiation_SelectsHighestMutuallySupportedVersion(
        NetworkProtocolVersion offered,
        NetworkProtocolVersion remote,
        NetworkProtocolVersion expected)
    {
        var connection = new TestConnection(_connectionCommon, Substitute.For<IMessageCenter>());

        connection.Negotiate(offered, remote);

        Assert.Equal(expected, connection.ProtocolVersion);
    }

    private sealed class TestConnection : Connection
    {
        private readonly TaskCompletionSource<Message> _sendFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestConnection(ConnectionCommon shared, IMessageCenter messageCenter)
            : this(new DefaultConnectionContext(), _ => Task.CompletedTask, shared, messageCenter)
        {
        }

        public TestConnection(
            ConnectionContext context,
            ConnectionDelegate middleware,
            ConnectionCommon shared,
            IMessageCenter messageCenter)
            : base(context, middleware, shared)
        {
            MessageCenter = messageCenter;
        }

        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;

        protected override IMessageCenter MessageCenter { get; }

        public int SendFailureCount { get; private set; }

        public Task<Message> SendFailure => _sendFailure.Task;

        public NetworkProtocolVersion ProtocolVersion => NetworkProtocolVersion;

        public bool HandleSendFailure(Message message, Exception exception) => HandleSendMessageFailure(message, exception);

        public void Negotiate(NetworkProtocolVersion offered, NetworkProtocolVersion remote)
            => NegotiateProtocolVersion(offered, remote);

        protected override bool PrepareMessageForSend(Message msg) => true;

        protected override void OnReceivedMessage(Message msg)
        {
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
            SendFailureCount++;
            _sendFailure.TrySetResult(message);
        }

        protected override void RecordMessageReceive(Message msg, int numTotalBytes, int headerBytes)
        {
        }

        protected override void RecordMessageSend(Message msg, int numTotalBytes, int headerBytes)
        {
        }

        protected override void RetryMessage(Message msg, Exception? ex = null)
        {
        }
    }

    private sealed class TestMessageCenter : IMessageCenter
    {
        private readonly TaskCompletionSource<Message> _sentMessage = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Message> SentMessage => _sentMessage.Task;

        public void DispatchLocalMessage(Message message) => _sentMessage.TrySetResult(message);

        public void SendMessage(Message msg) => _sentMessage.TrySetResult(msg);
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    private static DefaultConnectionContext CreateConnectionContext()
    {
        var input = new Pipe();
        var output = new Pipe();
        return new DefaultConnectionContext
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 11111),
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 22222),
            Transport = new DuplexPipe(input.Reader, output.Writer),
        };
    }
}
