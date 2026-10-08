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
        var events = new List<string>();
        count.When(value => value.Value = Arg.Any<int>()).Do(_ => events.Add("count"));
        DurableEnvelope? sentReply = null;
        context.When(value => value.Send(Arg.Any<DurableEnvelope>()))
            .Do(call =>
            {
                sentReply = call.Arg<DurableEnvelope>();
                events.Add("send");
            });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        var grain = new NotificationGrain(inbox, count);

        var handling = grain.HandleAsync(context, cancellation.Token);
        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "count", "send", "complete" }, events);
        await handling;

        var reply = Assert.IsType<DurableEnvelope>(sentReply);
        Assert.Equal(request.CorrelationKey, reply.CorrelationKey);
        Assert.Equal(sender, reply.ReceiverId);
        Assert.Equal(receiver, reply.SenderId);
        Assert.Equal("notifications/received", reply.RouteKey);
        Assert.True(reply.Data.TryGetBody<string>(out var body));
        Assert.Equal("received message", body);
        count.Received(1).Value = 8;
        context.Received(1).Send(reply);
        context.Received(1).Complete();
        await outbox.DidNotReceive().PrepareSendAsync(
            Arg.Any<IReadOnlyList<DurableEnvelope>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotificationHandling_PreMutationCancellationPreservesBusinessState()
    {
        var inbox = Substitute.For<IDurableInbox>();
        var count = Substitute.For<IDurableValue<int>>();
        var context = Substitute.For<IInboxHandlerContext>();
        var grain = new NotificationGrain(inbox, count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await grain.HandleAsync(context, cancellation.Token));

        count.DidNotReceive().Value = Arg.Any<int>();
        context.DidNotReceive().CreateEnvelope();
        context.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
        context.DidNotReceive().Complete();
    }

    [Fact]
    public async Task NotificationHandling_CancellationDuringLocalPreparationPreservesBusinessState()
    {
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("sender", "canceled-snippet");
        var receiver = GrainId.Create("notification", "canceled-snippet");
        var request = new DurableEnvelopeBuilder(sessions, sender)
            .To(receiver, "notifications")
            .WithReplyTo(sender)
            .WithBody("prepared message")
            .Build();
        var inbox = Substitute.For<IDurableInbox>();
        var count = Substitute.For<IDurableValue<int>>();
        count.Value.Returns(7);
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(request);
        using var cancellation = new CancellationTokenSource();
        context.CreateEnvelope().Returns(_ =>
        {
            cancellation.Cancel();
            return new DurableEnvelopeBuilder(sessions, receiver);
        });
        var grain = new NotificationGrain(inbox, count);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await grain.HandleAsync(context, cancellation.Token));

        context.Received(1).CreateEnvelope();
        count.DidNotReceive().Value = Arg.Any<int>();
        context.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
        context.DidNotReceive().Complete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationHandling_CancellationAfterFirstMutationCompletesBeforeMethodReturns(bool replyRequested)
    {
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        var sender = GrainId.Create("sender", "late-cancellation-snippet");
        var receiver = GrainId.Create("notification", "late-cancellation-snippet");
        var builder = new DurableEnvelopeBuilder(sessions, sender)
            .To(receiver, "notifications")
            .WithBody("prepared message");
        if (replyRequested)
        {
            builder.WithReplyTo(sender);
        }
        var request = builder.Build();
        var inbox = Substitute.For<IDurableInbox>();
        var count = Substitute.For<IDurableValue<int>>();
        count.Value.Returns(7);
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(request);
        context.CreateEnvelope().Returns(_ => new DurableEnvelopeBuilder(sessions, receiver));
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        count.When(value => value.Value = 8).Do(_ =>
        {
            Assert.False(cancellation.IsCancellationRequested);
            events.Add("count");
            cancellation.Cancel();
        });
        context.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(_ => events.Add("send"));
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        var grain = new NotificationGrain(inbox, count);

        var handling = grain.HandleAsync(context, cancellation.Token);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(replyRequested ? new[] { "count", "send", "complete" } : new[] { "count", "complete" }, events);
        await handling;
        count.Received(1).Value = 8;
        context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationWithoutReply_StagesCountAndCompletes()
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
        var events = new List<string>();
        count.When(value => value.Value = Arg.Any<int>()).Do(_ => events.Add("count"));
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        var grain = new NotificationGrain(inbox, count);

        var handling = grain.HandleAsync(context, TestContext.Current.CancellationToken);
        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "count", "complete" }, events);
        await handling;

        count.Received(1).Value = 8;
        context.DidNotReceive().CreateEnvelope();
        context.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
        context.Received(1).Complete();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task NotificationHandling_InvalidBodyLeavesBusinessAndCompletionUnchanged(string? body)
    {
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        var request = new DurableEnvelopeBuilder(sessions, GrainId.Create("sender", "invalid-snippet"))
            .To(GrainId.Create("notification", "invalid-snippet"), "notifications")
            .WithBody(body)
            .Build();
        var inbox = Substitute.For<IDurableInbox>();
        var count = Substitute.For<IDurableValue<int>>();
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(request);
        var grain = new NotificationGrain(inbox, count);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("nonempty string", exception.Message, StringComparison.Ordinal);
        count.DidNotReceive().Value = Arg.Any<int>();
        context.DidNotReceive().CreateEnvelope();
        context.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
        context.DidNotReceive().Complete();
    }

    public void Dispose() => _services.Dispose();
}
