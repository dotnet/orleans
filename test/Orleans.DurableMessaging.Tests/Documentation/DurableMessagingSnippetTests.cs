using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;
using NotificationGrain = Documentation.Grains.DurableMessaging.NotificationGrain;

namespace Orleans.DurableMessaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingSnippetTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();

    [Theory]
    [InlineData(null)]
    [InlineData("orders/2026/42")]
    public async Task NotificationReply_PreservesOptionalCorrelationKey(string? correlationKey)
    {
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("sender", "snippet");
        var receiver = GrainId.Create("notification", "snippet");
        var requestBuilder = new DurableEnvelopeBuilder(sessions, sender)
            .To(receiver, "notifications")
            .WithReplyTo(sender)
            .WithBody("received message");
        if (correlationKey is not null)
        {
            requestBuilder.WithCorrelationKey(correlationKey);
        }

        var request = requestBuilder.Build();
        var inbox = Substitute.For<IDurableInbox>();
        var outbox = Substitute.For<IDurableOutbox>();
        var count = Substitute.For<IDurableValue<int>>();
        count.Value.Returns(7);
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(request);
        context.Outbox.Returns(outbox);
        context.CreateEnvelope().Returns(_ => new DurableEnvelopeBuilder(sessions, receiver));
        using var cancellation = new CancellationTokenSource();
        DurableEnvelope? sentReply = null;
        context.When(value => value.Send(Arg.Any<DurableEnvelope>()))
            .Do(call => sentReply = call.Arg<DurableEnvelope>());
        var grain = new NotificationGrain(inbox, count);

        var apply = await grain.PrepareAsync(context, cancellation.Token);

        Assert.Null(sentReply);
        count.DidNotReceive().Value = Arg.Any<int>();
        context.DidNotReceive().Send(Arg.Any<DurableEnvelope>());

        apply();

        var reply = Assert.IsType<DurableEnvelope>(sentReply);
        Assert.Equal(request.CorrelationKey, reply.CorrelationKey);
        Assert.Equal(sender, reply.ReceiverId);
        Assert.Equal(receiver, reply.SenderId);
        Assert.Equal("notifications/received", reply.RouteKey);
        Assert.True(reply.Data.TryGetBody<string>(out var body));
        Assert.Equal("received message", body);
        count.Received(1).Value = 8;
        context.Received(1).Send(reply);
        await outbox.DidNotReceive().PrepareSendAsync(
            Arg.Any<IReadOnlyList<DurableEnvelope>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotificationPreparation_CancellationPreservesBusinessState()
    {
        var inbox = Substitute.For<IDurableInbox>();
        var count = Substitute.For<IDurableValue<int>>();
        var context = Substitute.For<IInboxHandlerContext>();
        var grain = new NotificationGrain(inbox, count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await grain.PrepareAsync(context, cancellation.Token));

        count.DidNotReceive().Value = Arg.Any<int>();
        context.DidNotReceive().CreateEnvelope();
        context.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
    }

    [Fact]
    public async Task NotificationWithoutReply_StagesCountInReturnedAction()
    {
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        var request = new DurableEnvelopeBuilder(sessions, GrainId.Create("sender", "snippet"))
            .To(GrainId.Create("notification", "snippet"), "notifications")
            .WithBody("received message")
            .Build();
        var inbox = Substitute.For<IDurableInbox>();
        var count = Substitute.For<IDurableValue<int>>();
        count.Value.Returns(7);
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(request);
        var grain = new NotificationGrain(inbox, count);

        var apply = await grain.PrepareAsync(context, TestContext.Current.CancellationToken);

        count.DidNotReceive().Value = Arg.Any<int>();
        apply();
        count.Received(1).Value = 8;
        context.DidNotReceive().CreateEnvelope();
        context.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
    }

    public void Dispose() => _services.Dispose();
}
