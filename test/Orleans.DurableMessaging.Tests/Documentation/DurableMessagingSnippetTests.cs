using System.Buffers;
using Documentation.Grains.DurableMessaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingSnippetTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly GrainId Sender = GrainId.Create("sender", "snippet");
    private static readonly GrainId Receiver = GrainId.Create("notification", "snippet");
    private static readonly HierarchicalKey Command = HierarchicalKey.Create("notifications", "42");
    private Serializer Serializer => _services.GetRequiredService<Serializer>();
    private readonly List<DurableEnvelope> _owned = [];
    private readonly List<DurableMessageWriter> _writers = [];

    private DurableMessageType<T> Type<T>() => new(typeof(T).Name, _services.GetRequiredService<Serializer<T>>());

    private DurableMessageWriter Writer(GrainId sender)
    {
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(sender);
        var writer = new DurableMessageWriter(context);
        _writers.Add(writer);
        return writer;
    }

    private DurableEnvelope Own(DurableEnvelope envelope)
    {
        _owned.Add(envelope);
        return envelope;
    }

    [Fact]
    public async Task NotificationReply_DerivesResultIdentityFromApplicationCommand()
    {
        var attempt = Create(new Notify("received message", Sender));
        var handling = attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "count", "send", "complete" }, attempt.Events);
        await handling;
        var reply = Assert.Single(attempt.Output);
        Assert.Equal(Sender, reply.ReceiverId);
        Assert.Equal(Receiver, reply.SenderId);
        Assert.Equal(Command.CreateChildKey("result"), reply.MessageId);
        Assert.Equal(Type<NotificationReceived>().Subject, reply.Subject);
        Assert.Equal(new NotificationReceived("received message", Command), Type<NotificationReceived>().Decode(reply));
        Assert.Equal(8, attempt.Count.Value);
        attempt.Inbox.Received(1).RegisterHandler(attempt.Grain);
        attempt.Outbox.Received(1).Send(reply);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationHandling_PreMutationCancellationPreservesBusinessState()
    {
        var attempt = Create(new Notify("received message", Sender));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_CancellationDuringLocalPreparationPreservesBusinessState()
    {
        var attempt = Create(new Notify("prepared message", Sender));
        using var cancellation = new CancellationTokenSource();
        attempt.Count.OnRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, cancellation.Token));

        attempt.Count.OnRead = null;
        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationHandling_CancellationAfterFirstMutationCompletesBeforeMethodReturns(bool replyRequested)
    {
        var attempt = Create(new Notify("prepared message", replyRequested ? Sender : null));
        using var cancellation = new CancellationTokenSource();
        attempt.Count.OnWrite = () =>
        {
            Assert.False(cancellation.IsCancellationRequested);
            cancellation.Cancel();
        };

        var handling = attempt.Grain.HandleAsync(attempt.Context, cancellation.Token);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(replyRequested ? new[] { "count", "send", "complete" } : new[] { "count", "complete" }, attempt.Events);
        await handling;
        Assert.Equal(8, attempt.Count.Value);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationWithoutReply_StagesCountAndCompletes()
    {
        var attempt = Create(new Notify("received message"));
        var handling = attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "count", "complete" }, attempt.Events);
        await handling;
        Assert.Equal(8, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        attempt.Context.Received(1).Complete();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task NotificationHandling_InvalidBodyLeavesBusinessAndCompletionUnchanged(string? body)
    {
        var attempt = Create(new Notify(body));
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Contains("nonempty string", exception.Message, StringComparison.Ordinal);
        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_UnexpectedSubjectPreservesBusinessState()
    {
        var attempt = Create(new Notify("prepared"));
        var unexpected = Own(Writer(Sender).Create(
            Type<NotificationReceived>(), Command, Receiver, new NotificationReceived("receipt", Command)));
        attempt.Context.Envelope.Returns(unexpected);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await attempt.Grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Events);
        Assert.Empty(attempt.Output);
    }

    [Fact]
    public void ShipmentPackage_EntriesRemainValidWhileDecodedOwnerIsRetained()
    {
        var request = new ReserveStock(3, Sender);
        byte[] manifest = [1, 2, 3];
        BufferPackage retained;
        using (var encoder = new ArcBufferWriter())
        using (var encoded = ShipmentPackage.Encode(Serializer, encoder, request, manifest))
        using (var package = ShipmentPackage.Decode(Serializer, encoded))
        {
            manifest[0] = 99;
            Assert.Equal(request, ShipmentPackage.ReadReservation(Serializer, package));
            Assert.Equal(new[] { "manifest", "reservation" }, package.Keys.Order().ToArray());
            Assert.False(package.TryGetBytes("missing", out _));
            Assert.Same(encoded.First, package.Buffer.First);
            retained = package.Retain();
        }

        using (retained)
        {
            Assert.Equal(request, ShipmentPackage.ReadReservation(Serializer, retained));
            Assert.True(retained.TryGetBytes("manifest", out var bytes));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes.ToArray());
        }
        Assert.Throws<ObjectDisposedException>(() => retained.TryGetBytes("manifest", out _));
    }

    [Fact]
    public void ArcPayload_ConsumedSlicesSharePagesAndSurviveEncoderDisposal()
    {
        var serializer = _services.GetRequiredService<Serializer<Notify>>();
        var message = new Notify("retained", Sender);
        ArcBuffer first;
        ArcBuffer second;
        using (var encoder = new ArcBufferWriter())
        {
            first = ArcPayloadEncoder.Encode(serializer, encoder, message);
            try
            {
                second = ArcPayloadEncoder.Encode(serializer, encoder, message with { Text = "different bytes" });
            }
            catch
            {
                first.Dispose();
                throw;
            }
        }

        using (first)
        using (second)
        {
            Assert.Same(first.First, second.First);
            Assert.True(second.Offset >= first.Offset + first.Length);
            Assert.Equal(message, ArcPayloadEncoder.DecodeRetained(Serializer, first));
            Assert.Equal("different bytes", serializer.Deserialize(second)!.Text);
            Assert.Equal(message, serializer.Deserialize(first));
        }
    }

    [Fact]
    public void Envelope_RetainOwnsIndependentPinAndSerializationDoesNotConsumePayload()
    {
        DurableEnvelope retained;
        using (var writer = Writer(Sender))
        using (var envelope = writer.Create(Type<Notify>(), Command, Receiver, new Notify("owned")))
        {
            retained = envelope.Retain();
        }

        using (retained)
        {
            using var encoder = new ArcBufferWriter();
            Serializer.Serialize(retained, encoder);
            using var first = encoder.ConsumeSlice(encoder.Length);
            Serializer.Serialize(retained, encoder);
            using var second = encoder.ConsumeSlice(encoder.Length);
            Assert.Equal(first.ToArray(), second.ToArray());
            using var decoded = Serializer.Deserialize<DurableEnvelope>(first);
            Assert.Equal(retained.MessageId, decoded.MessageId);
            Assert.Equal(retained.Subject, decoded.Subject);
            Assert.Equal(new Notify("owned"), Type<Notify>().Decode(decoded));
            Assert.Equal(new Notify("owned"), Type<Notify>().Decode(retained));
        }
    }

    [Fact]
    public async Task NotificationHandling_StagingFailureReleasesLocalReplyAndLeavesCompletionUnstaged()
    {
        var inbox = Substitute.For<IDurableInbox>();
        var outbox = Substitute.For<IDurableOutbox>();
        var context = Substitute.For<IInboxHandlerContext>();
        var input = Own(Writer(Sender).Create(Type<Notify>(), Command, Receiver, new Notify("prepared", Sender)));
        context.Envelope.Returns(input);
        ArcBuffer borrowedReply = default;
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            borrowedReply = call.Arg<DurableEnvelope>().Payload;
            throw new IOException("staging failed");
        });
        using (var writer = Writer(Receiver))
        {
            var grain = new NotificationGrain(inbox, outbox, writer, Type<Notify>(), Type<NotificationReceived>(), new TestCount([]));
            await Assert.ThrowsAsync<IOException>(async () =>
                await grain.HandleAsync(context, TestContext.Current.CancellationToken));
            context.DidNotReceive().Complete();
            Assert.NotEqual(0, borrowedReply.Length);
        }
        Assert.Throws<InvalidOperationException>(() => borrowedReply.ToArray());
    }

    private Attempt Create(Notify message)
    {
        var inbox = Substitute.For<IDurableInbox>();
        var outbox = Substitute.For<IDurableOutbox>();
        var context = Substitute.For<IInboxHandlerContext>();
        var input = Own(Writer(Sender).Create(Type<Notify>(), Command, Receiver, message));
        context.Envelope.Returns(input);
        var events = new List<string>();
        var output = new List<DurableEnvelope>();
        var count = new TestCount(events);
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            output.Add(Own(call.Arg<DurableEnvelope>().Retain()));
            events.Add("send");
        });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        var writer = Writer(Receiver);
        var grain = new NotificationGrain(inbox, outbox, writer, Type<Notify>(), Type<NotificationReceived>(), count);
        return new(grain, inbox, outbox, context, count, writer, output, events);
    }

    public void Dispose()
    {
        foreach (var envelope in _owned) envelope.Dispose();
        foreach (var writer in _writers) writer.Dispose();
        _services.Dispose();
    }

    private sealed record Attempt(NotificationGrain Grain, IDurableInbox Inbox, IDurableOutbox Outbox,
        IInboxHandlerContext Context, TestCount Count, DurableMessageWriter Writer, List<DurableEnvelope> Output, List<string> Events);

    private sealed class TestCount(List<string> events) : IDurableValue<int>
    {
        private int _value = 7;
        public Action? OnRead { get; set; }
        public Action? OnWrite { get; set; }
        public int Value
        {
            get { OnRead?.Invoke(); return _value; }
            set { _value = value; events.Add("count"); OnWrite?.Invoke(); }
        }
    }
}
