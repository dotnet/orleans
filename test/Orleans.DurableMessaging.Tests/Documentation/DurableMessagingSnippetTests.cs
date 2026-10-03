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
        var batch = Substitute.For<IPreparedOutboxBatch>();
        using var cancellation = new CancellationTokenSource();
        DurableEnvelope? preparedReply = null;
        outbox.PrepareSendAsync(Arg.Any<IReadOnlyList<DurableEnvelope>>(), cancellation.Token)
            .Returns(call =>
            {
                preparedReply = Assert.Single(call.ArgAt<IReadOnlyList<DurableEnvelope>>(0));
                return ValueTask.FromResult(batch);
            });
        var grain = new NotificationGrain(inbox, count);

        var apply = await grain.PrepareAsync(context, cancellation.Token);

        var reply = Assert.IsType<DurableEnvelope>(preparedReply);
        Assert.Equal(request.CorrelationKey, reply.CorrelationKey);
        Assert.Equal(sender, reply.ReceiverId);
        Assert.Equal(receiver, reply.SenderId);
        Assert.Equal("notifications/received", reply.RouteKey);
        Assert.True(reply.Data.TryGetBody<string>(out var body));
        Assert.Equal("received message", body);
        count.DidNotReceive().Value = Arg.Any<int>();
        context.DidNotReceive().Send(Arg.Any<IPreparedOutboxBatch>());

        apply();

        count.Received(1).Value = 8;
        context.Received(1).Send(batch);
        await outbox.Received(1).PrepareSendAsync(Arg.Any<IReadOnlyList<DurableEnvelope>>(), cancellation.Token);
    }

    public void Dispose() => _services.Dispose();
}
