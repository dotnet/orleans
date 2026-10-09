using Documentation.Grains.DurableMessaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingRecipeTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly GrainId Sender = GrainId.Create("order", "42");
    private static readonly GrainId Receiver = GrainId.Create("inventory", "widget");
    private static readonly HierarchicalKey Command = HierarchicalKey.Create("orders", "42", "reserve");
    private readonly List<DurableEnvelope> _owned = [];
    private readonly List<DurableMessageWriter> _writers = [];
    private readonly IDurableOutbox _outbox = Substitute.For<IDurableOutbox>();
    private (List<DurableEnvelope> Output, List<string> Events) _activeAttempt;

    public DurableMessagingRecipeTests()
    {
        _outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            _activeAttempt.Output.Add(Own(call.Arg<DurableEnvelope>().Retain()));
            _activeAttempt.Events.Add("send");
        });
    }

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
    public void HierarchicalKeys_IsolateTenantsStepsAndSegmentBoundaries()
    {
        var orderId = Guid.Parse("d63b9220-894b-4faf-86ce-cc21a63559a9");
        var order = OrderOperationKeys.Order("acme/eu", orderId);
        var reservation = OrderOperationKeys.Reservation(order, "widget/blue");
        var same = OrderOperationKeys.Reservation(OrderOperationKeys.Order("acme/eu", orderId), "widget/blue");

        Assert.True(reservation == same);
        Assert.Equal(reservation.GetHashCode(), same.GetHashCode());
        Assert.True(order.IsAncestorOf(reservation));
        Assert.True(order.IsAncestorOf(OrderOperationKeys.Charge(order)));
        Assert.True(reservation != OrderOperationKeys.Charge(order));
        Assert.False(OrderOperationKeys.Order("acme/us", orderId).IsAncestorOf(reservation));
        Assert.False(HierarchicalKey.Create("orders", "42").IsAncestorOf(HierarchicalKey.Create("orders", "420", "payment")));
        var escaped = HierarchyExample.Create();
        Assert.Equal(@"orders/42/inventory/widget\/blue/reserve", escaped.Step.ToString());
        Assert.True(escaped.Order.IsAncestorOf(escaped.Step));
        Assert.Equal(escaped.Step, HierarchicalKey.Parse(escaped.Step.ToString(), null));
    }

    [Theory]
    [InlineData(10, 3, true, 7)]
    [InlineData(2, 3, false, 2)]
    public async Task Reservation_OneCommandStagesDecisionAndDeterministicResult(
        int available, int quantity, bool reserved, int expectedStock)
    {
        var stock = new TestValue<int> { Value = available };
        var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), _outbox, Writer(Receiver),
            Type<ReserveStock>(), Type<ReservationResult>(), Substitute.For<IDurableStateManager>(), stock);
        var request = new ReserveStock(quantity, Sender);
        var attempt = CreateContext(request, Command);

        var handling = grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        await handling;
        Assert.Equal(expectedStock, stock.Value);
        var reply = Assert.Single(attempt.Output);
        Assert.Equal(Command.CreateChildKey("result"), reply.MessageId);
        Assert.Equal(Sender, reply.ReceiverId);
        Assert.Equal(new ReservationResult(quantity, reserved), ReadBody<ReservationResult>(reply));
        Assert.Equal(new[] { "send", "complete" }, attempt.Events);
    }

    [Fact]
    public async Task Reservation_LocalCancellationPreservesStockAndCompletion()
    {
        var stock = new TestValue<int> { Value = 10 };
        var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), _outbox, Writer(Receiver),
            Type<ReserveStock>(), Type<ReservationResult>(), Substitute.For<IDurableStateManager>(), stock);
        var attempt = CreateContext(new ReserveStock(3, Sender), Command);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await grain.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(10, stock.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payment_AmbiguousProviderOutcomeRetriesOriginalKeyOnce(bool cancelAfterProviderSuccess)
    {
        var results = new TestDictionary<HierarchicalKey, PaymentResult>();
        using var cancellation = new CancellationTokenSource();
        var gateway = new IdempotentGateway
        {
            LoseFirstResponse = !cancelAfterProviderSuccess,
            AfterFirstCharge = cancelAfterProviderSuccess ? cancellation.Cancel : null
        };
        var grain = new PaymentGrain(Substitute.For<IDurableInbox>(), _outbox, Writer(Receiver),
            Type<ChargePayment>(), Type<PaymentResult>(), gateway, results);
        var request = new ChargePayment(12.5m, "USD", Sender);
        var key = HierarchicalKey.Create("tenants", "acme", "orders", "42", "charge");
        var first = CreateContext(request, key);
        if (cancelAfterProviderSuccess)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await grain.HandleAsync(first.Context, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await grain.HandleAsync(first.Context, cancellation.Token));
        }
        Assert.Empty(results);
        Assert.Empty(first.Output);
        Assert.Empty(first.Events);
        Assert.Equal(1, gateway.Charges);

        var retry = CreateContext(request, key);
        await grain.HandleAsync(retry.Context, TestContext.Current.CancellationToken);

        Assert.Equal(key, retry.Context.Envelope.MessageId);
        Assert.Equal(1, gateway.Charges);
        Assert.Equal(new[] { key.ToString(), key.ToString() }, gateway.Calls);
        var outcome = Assert.Single(results).Value;
        Assert.Equal(request, outcome.Request);
        Assert.True(outcome.Charged);
        Assert.Equal("provider-charge-1", outcome.ProviderReference);
        Assert.Equal(outcome, await grain.GetResultAsync(key));
        var reply = Assert.Single(retry.Output);
        Assert.Equal(key.CreateChildKey("result"), reply.MessageId);
        Assert.Equal(outcome, ReadBody<PaymentResult>(reply));
        Assert.Equal(new[] { "send", "complete" }, retry.Events);
    }

    [Fact]
    public async Task Payment_PreparationAwaitKeepsBorrowedInputAndStagesOwnedReplyBeforeReturn()
    {
        var results = new TestDictionary<HierarchicalKey, PaymentResult>();
        var gateway = Substitute.For<IIdempotentPaymentGateway>();
        var request = new ChargePayment(12.5m, "USD", Sender);
        var outcome = new PaymentResult(request, "provider-charge-1", true);
        var prepared = new TaskCompletionSource<PaymentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ChargeAsync(Command.ToString(), request, Arg.Any<CancellationToken>()).Returns(prepared.Task);
        var attempt = CreateContext(request, Command);
        var writer = Writer(Receiver);
        var grain = new PaymentGrain(Substitute.For<IDurableInbox>(), _outbox, writer,
            Type<ChargePayment>(), Type<PaymentResult>(), gateway, results);

        var handling = grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
        Assert.False(handling.IsCompleted);
        Assert.Empty(results);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
        Assert.Equal(request, ReadBody<ChargePayment>(attempt.Context.Envelope));
        prepared.SetResult(outcome);
        await handling;
        Assert.Equal(new[] { "send", "complete" }, attempt.Events);
        writer.Dispose();
        Assert.Equal(outcome, ReadBody<PaymentResult>(Assert.Single(attempt.Output)));
        Assert.Equal(outcome, results[Command]);
    }

    [Theory]
    [InlineData(9, 99, 10, 7)]
    [InlineData(10, 7, 10, 7)]
    [InlineData(12, 4, 12, 4)]
    public async Task Projection_UnorderedSnapshotsConvergeToLatestCompleteValue(
        long incomingVersion, int incomingStock, long expectedVersion, int expectedStock)
    {
        var snapshot = new TestValue<StockSnapshot> { Value = new StockSnapshot(10, 7) };
        var grain = new StockProjectionGrain(Substitute.For<IDurableInbox>(), Type<StockSnapshot>(), snapshot);
        var attempt = CreateContext(new StockSnapshot(incomingVersion, incomingStock), Command);

        var handling = grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        await handling;
        Assert.Equal(new StockSnapshot(expectedVersion, expectedStock), await grain.GetSnapshotAsync());
        Assert.Equal(new[] { "complete" }, attempt.Events);
        Assert.Empty(attempt.Output);
    }

    [Fact]
    public async Task Projection_ConflictingVersionPreservesOriginalSnapshot()
    {
        var snapshot = new TestValue<StockSnapshot> { Value = new StockSnapshot(10, 7) };
        var grain = new StockProjectionGrain(Substitute.For<IDurableInbox>(), Type<StockSnapshot>(), snapshot);
        var attempt = CreateContext(new StockSnapshot(10, 99), Command);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(new StockSnapshot(10, 7), snapshot.Value);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task OrderDispatcher_MultipleSubjectsRecordOneOutcomePerCommand()
    {
        var outcomes = new TestDictionary<HierarchicalKey, OrderOutcome>();
        var inbox = Substitute.For<IDurableInbox>();
        IInboxHandler dispatcher = null!;
        inbox.When(value => value.RegisterHandler(Arg.Any<IInboxHandler>())).Do(call => dispatcher = call.Arg<IInboxHandler>());
        var grain = new OrderOutcomesGrain(inbox, Type<ReservationResult>(), Type<PaymentResult>(), outcomes);
        var reservation = new ReservationResult(3, true);
        var payment = new PaymentResult(new ChargePayment(12.5m, "USD", Sender), "provider-charge-1", true);
        var reservationId = Command.CreateChildKey("result");
        var paymentId = HierarchicalKey.Create("orders", "42", "charge", "result");
        var first = CreateContext(reservation, reservationId);
        var second = CreateContext(payment, paymentId);

        foreach (var attempt in new[] { first, second })
        {
            var handling = dispatcher.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
            Assert.True(handling.IsCompletedSuccessfully);
            await handling;
            Assert.Equal(new[] { "complete" }, attempt.Events);
            Assert.Empty(attempt.Output);
        }
        Assert.IsType<DurableInboxDispatcher>(dispatcher);
        Assert.Equal(2, await grain.GetCompletedStepCountAsync());
        Assert.Equal(reservation, outcomes[reservationId]);
        Assert.Equal(payment, outcomes[paymentId]);
        inbox.Received(1).RegisterHandler(dispatcher);
    }

    [Fact]
    public async Task Campaign_FanoutStagesFrozenRecipientIntentsBeforeAwaitingAcknowledgement()
    {
        var campaigns = new TestDictionary<Guid, NotificationCampaign>();
        var outbox = Substitute.For<IDurableOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(Sender);
        var grain = new CampaignGrain(outbox, state, campaigns, Type<Notify>(), Writer(Sender), grainContext);
        var id = Guid.Parse("3dd3fbec-0197-47cf-9233-92ecbddcd057");
        GrainId[] original = [GrainId.Create("notification", "alice"), GrainId.Create("notification", "bob")];
        var recipients = original.ToArray();
        var outputs = new List<DurableEnvelope>();
        var events = new List<string>();
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            outputs.Add(Own(call.Arg<DurableEnvelope>().Retain()));
            events.Add("send");
        });
        state.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Assert.Equal(2, outputs.Count);
            Assert.Single(campaigns);
            events.Add("write");
            return new ValueTask(acknowledgement.Task);
        });

        var publish = grain.PublishAsync(id, "campaign text", recipients);
        Assert.False(publish.IsCompleted);
        Assert.Equal(new[] { "send", "send", "write" }, events);
        recipients[0] = GrainId.Create("notification", "mutated-source");
        Assert.Equal(original, campaigns[id].Recipients);
        Assert.Equal(original, outputs.Select(envelope => envelope.ReceiverId));
        Assert.Equal(2, outputs.Select(envelope => envelope.MessageId).Distinct().Count());
        var root = HierarchicalKey.Create("campaigns", id.ToString("N"));
        for (var index = 0; index < outputs.Count; index++)
        {
            var envelope = outputs[index];
            Assert.Equal(Sender, envelope.SenderId);
            Assert.Equal(root.CreateChildKey(original[index].ToString()), envelope.MessageId);
            Assert.Equal("campaign text", ReadBody<Notify>(envelope).Text);
            Assert.Null(ReadBody<Notify>(envelope).ResponseDestination);
        }
        acknowledgement.SetResult();
        await publish;
        await grain.PublishAsync(id, "campaign text", original);
        Assert.Equal(2, outputs.Count);
        Assert.Single(campaigns);
        Assert.Equal(new[] { "send", "send", "write", "write" }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_ConflictingSubmissionPreservesRecordedContentAndIntents(bool changeRecipients)
    {
        var id = Guid.Parse("3dd3fbec-0197-47cf-9233-92ecbddcd057");
        GrainId[] original = [GrainId.Create("notification", "alice")];
        var campaign = new NotificationCampaign("original", original);
        var campaigns = new TestDictionary<Guid, NotificationCampaign> { [id] = campaign };
        var outbox = Substitute.For<IDurableOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(Sender);
        var grain = new CampaignGrain(outbox, state, campaigns, Type<Notify>(), Writer(Sender), context);

        await Assert.ThrowsAsync<ArgumentException>(() => grain.PublishAsync(id,
            changeRecipients ? "original" : "changed",
            changeRecipients ? [GrainId.Create("notification", "bob")] : original));

        Assert.Same(campaign, Assert.Single(campaigns).Value);
        Assert.Equal(original, campaign.Recipients);
        outbox.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    private (IInboxHandlerContext Context, List<DurableEnvelope> Output, List<string> Events) CreateContext<T>(T body, HierarchicalKey key)
    {
        var envelope = Own(Writer(Sender).Create(Type<T>(), key, Receiver, body));
        var context = Substitute.For<IInboxHandlerContext>();
        var output = new List<DurableEnvelope>();
        var events = new List<string>();
        context.Envelope.Returns(_ =>
        {
            _activeAttempt = (output, events);
            return envelope;
        });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        return (context, output, events);
    }

    private T ReadBody<T>(DurableEnvelope envelope) => Type<T>().Decode(envelope);

    public void Dispose()
    {
        foreach (var envelope in _owned) envelope.Dispose();
        foreach (var writer in _writers) writer.Dispose();
        _services.Dispose();
    }

    private sealed class TestDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    private sealed class TestValue<T> : IDurableValue<T>
    {
        public T? Value { get; set; }
    }

    private sealed class IdempotentGateway : IIdempotentPaymentGateway
    {
        private readonly Dictionary<string, PaymentResult> _outcomes = [];
        public List<string> Calls { get; } = [];
        public int Charges { get; private set; }
        public bool LoseFirstResponse { get; init; }
        public Action? AfterFirstCharge { get; init; }

        public Task<PaymentResult> ChargeAsync(string idempotencyKey, ChargePayment request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(idempotencyKey);
            if (!_outcomes.TryGetValue(idempotencyKey, out var result))
            {
                Charges++;
                result = new PaymentResult(request, "provider-charge-1", true);
                _outcomes.Add(idempotencyKey, result);
                AfterFirstCharge?.Invoke();
                if (LoseFirstResponse)
                {
                    return Task.FromException<PaymentResult>(new IOException("The provider committed the charge; the response was lost."));
                }
            }
            Assert.Equal(request, result.Request);
            return Task.FromResult(result);
        }
    }
}
