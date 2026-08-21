using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
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

    public ConnectionTests(TestEnvironmentFixture fixture)
    {
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

    private sealed class TestConnection(ConnectionCommon shared, IMessageCenter messageCenter)
        : Connection(new DefaultConnectionContext(), _ => Task.CompletedTask, shared)
    {
        protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;

        protected override IMessageCenter MessageCenter => messageCenter;

        public bool HandleSendFailure(Message message, Exception exception) => HandleSendMessageFailure(message, exception);

        protected override bool PrepareMessageForSend(Message msg) => true;

        protected override void OnReceivedMessage(Message msg)
        {
        }

        protected override void OnSendMessageFailure(Message message, string error)
        {
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
}
