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
    private Serializer Serializer => _services.GetRequiredService<Serializer>();
    private static readonly GrainId Receiver = GrainId.Create("inventory", "widget");
    private readonly ApplicationPayload _payload;
    private readonly List<DurableEnvelope> _owned = [];

    public DurableMessagingRecipeTests() => _payload = new(Serializer);

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

        Assert.Equal(reservation, same);
        Assert.Equal(reservation.GetHashCode(), same.GetHashCode());
        Assert.Contains("acme%2Feu/orders", order.ToString(), StringComparison.Ordinal);
        Assert.Contains("inventory/widget%2Fblue/reserve", reservation.ToString(), StringComparison.Ordinal);
        Assert.True(order.IsAncestorOf(reservation));
        Assert.True(order.IsAncestorOf(OrderOperationKeys.Charge(order)));
        Assert.False(reservation.Equals(OrderOperationKeys.Charge(order)));
        Assert.False(OrderOperationKeys.Order("acme/us", orderId).IsAncestorOf(reservation));
        Assert.False(HierarchicalKey.Create("orders/42").IsAncestorOf(HierarchicalKey.Create("orders/420/payment")));
        var escaped = HierarchyExample.Create();
        Assert.Equal(@"orders/42/inventory/widget\/blue/reserve", escaped.Step.ToString());
        Assert.True(escaped.Order.IsAncestorOf(escaped.Step));
        var segments = new List<string>();
        foreach (var segment in escaped.Step)
        {
            segments.Add(segment.ToString());
        }
        Assert.Equal(new[] { "orders", "42", "inventory", @"widget\/blue", "reserve" }, segments);
    }

    [Theory]
    [InlineData(10, 3, true, 7)]
    [InlineData(2, 3, false, 2)]
    public async Task Reservation_FreshMessageDuplicateReusesOriginalDecision(
        int available, int quantity, bool reserved, int expectedStock)
    {
        var stock = new TestValue<int> { Value = available };
        var ledger = new TestDictionary<HierarchicalKey, ReservationResult>();
        using var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), Outbox, Serializer, Substitute.For<IDurableStateManager>(), stock, ledger);
        var request = new ReserveStock(HierarchicalKey.Create("orders/42/inventory/widget/reserve"), quantity, Sender);
        var first = CreateContext(request, request.OperationKey);
        var second = CreateContext(request, request.OperationKey);
        Assert.NotEqual(first.Context.Envelope.MessageId, second.Context.Envelope.MessageId);

        await grain.HandleAsync(first.Context, TestContext.Current.CancellationToken);
        Assert.Equal(expectedStock, stock.Value);
        stock.Value = expectedStock + 100;
        await grain.HandleAsync(second.Context, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStock + 100, stock.Value);
        var outcome = Assert.Single(ledger).Value;
        Assert.Equal(new ReservationResult(request.OperationKey, quantity, reserved), outcome);
        Assert.Equal(outcome, ReadBody<ReservationResult>(Assert.Single(first.Output)));
        Assert.Equal(outcome, ReadBody<ReservationResult>(Assert.Single(second.Output)));
        Assert.Equal(new[] { "send", "complete" }, first.Events);
        Assert.Equal(new[] { "send", "complete" }, second.Events);
        Assert.Equal(Sender, second.Output[0].ReceiverId);
        Assert.Equal(request.OperationKey, ReadBody<ReservationResult>(second.Output[0]).OperationKey);
    }

    [Fact]
    public async Task Reservation_ConflictingKeyReusePreservesStockAndOutcome()
    {
        var key = HierarchicalKey.Create("orders/42/inventory/widget/reserve");
        var original = new ReservationResult(key, 3, true);
        var stock = new TestValue<int> { Value = 7 };
        var ledger = new TestDictionary<HierarchicalKey, ReservationResult> { [key] = original };
        using var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), Outbox, Serializer, Substitute.For<IDurableStateManager>(), stock, ledger);
        var request = new ReserveStock(key, 4, Sender);
        var attempt = CreateContext(request, key);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(7, stock.Value);
        Assert.Equal(original, Assert.Single(ledger).Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task Reservation_LocalCancellationPreservesStockAndLedger()
    {
        var stock = new TestValue<int> { Value = 10 };
        var ledger = new TestDictionary<HierarchicalKey, ReservationResult>();
        using var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), Outbox, Serializer, Substitute.For<IDurableStateManager>(), stock, ledger);
        var request = new ReserveStock(HierarchicalKey.Create("orders/42/inventory/widget/reserve"), 3, Sender);
        var attempt = CreateContext(request, request.OperationKey);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await grain.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(10, stock.Value);
        Assert.Empty(ledger);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payment_AmbiguousProviderOutcomeRetriesOriginalKeyOnce(bool cancelAfterProviderSuccess)
    {
        var ledger = new TestDictionary<HierarchicalKey, PaymentResult>();
        using var cancellation = new CancellationTokenSource();
        var gateway = new IdempotentGateway
        {
            LoseFirstResponse = !cancelAfterProviderSuccess,
            AfterFirstCharge = cancelAfterProviderSuccess ? cancellation.Cancel : null
        };
        using var grain = new PaymentGrain(Substitute.For<IDurableInbox>(), Outbox, Serializer, gateway, ledger);
        var request = new ChargePayment(HierarchicalKey.Create("tenants/acme/orders/42/payment/charge"), 12.5m, "USD", Sender);
        var first = CreateContext(request, request.OperationKey);
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
        Assert.Empty(ledger);
        Assert.Empty(first.Output);
        Assert.Empty(first.Events);
        Assert.Equal(1, gateway.Charges);

        var retry = CreateContext(request, request.OperationKey);
        await grain.HandleAsync(retry.Context, TestContext.Current.CancellationToken);
        var duplicate = CreateContext(request, request.OperationKey);
        await grain.HandleAsync(duplicate.Context, TestContext.Current.CancellationToken);

        Assert.Equal(1, gateway.Charges);
        Assert.Equal(new[] { request.OperationKey.ToString(), request.OperationKey.ToString() }, gateway.Calls);
        var outcome = Assert.Single(ledger).Value;
        Assert.Equal(request, outcome.Request);
        Assert.True(outcome.Charged);
        Assert.Equal("provider-charge-1", outcome.ProviderReference);
        Assert.Equal(outcome, await grain.GetResultAsync(request.OperationKey));
        Assert.Equal(outcome, ReadBody<PaymentResult>(Assert.Single(retry.Output)));
        Assert.Equal(outcome, ReadBody<PaymentResult>(Assert.Single(duplicate.Output)));
        Assert.Equal(new[] { "send", "complete" }, retry.Events);
        Assert.Equal(new[] { "send", "complete" }, duplicate.Events);
    }

    [Fact]
    public async Task Payment_PreparationAwaitKeepsBorrowedInputAndStagesOwnedReplyBeforeReturn()
    {
        var ledger = new TestDictionary<HierarchicalKey, PaymentResult>();
        var gateway = Substitute.For<IIdempotentPaymentGateway>();
        var request = new ChargePayment(HierarchicalKey.Create("orders/42/payment/charge"), 12.5m, "USD", Sender);
        var outcome = new PaymentResult(request, "provider-charge-1", true);
        var prepared = new TaskCompletionSource<PaymentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ChargeAsync(request.OperationKey.ToString(), request, Arg.Any<CancellationToken>())
            .Returns(prepared.Task);
        var attempt = CreateContext(request, request.OperationKey);
        using (var grain = new PaymentGrain(Substitute.For<IDurableInbox>(), Outbox, Serializer, gateway, ledger))
        {
            var handling = grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
            Assert.False(handling.IsCompleted);
            Assert.Empty(ledger);
            Assert.Empty(attempt.Output);
            Assert.Empty(attempt.Events);
            // The inbox-owned input remains borrowed across actual asynchronous preparation.
            Assert.Equal(request, ReadBody<ChargePayment>(attempt.Context.Envelope));
            prepared.SetResult(outcome);
            await handling;
            Assert.Equal(new[] { "send", "complete" }, attempt.Events);
        }
        // The outbox substitute retained its pin; local reply and grain encoder are gone.
        Assert.Equal(outcome, ReadBody<PaymentResult>(Assert.Single(attempt.Output)));
        Assert.Equal(outcome, Assert.Single(ledger).Value);
    }

    [Fact]
    public async Task Payment_ConflictingKeyReusePreservesOriginalProviderOutcome()
    {
        var ledger = new TestDictionary<HierarchicalKey, PaymentResult>();
        var gateway = new IdempotentGateway();
        using var grain = new PaymentGrain(Substitute.For<IDurableInbox>(), Outbox, Serializer, gateway, ledger);
        var original = new ChargePayment(HierarchicalKey.Create("orders/42/payment/charge"), 12.5m, "USD", Sender);
        var first = CreateContext(original, original.OperationKey);
        await grain.HandleAsync(first.Context, TestContext.Current.CancellationToken);
        var conflicting = original with { Amount = 25m };
        var attempt = CreateContext(conflicting, conflicting.OperationKey);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(1, gateway.Charges);
        Assert.Single(gateway.Calls);
        Assert.Equal(original, Assert.Single(ledger).Value.Request);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Theory]
    [InlineData(9, 99, 10, 7)]
    [InlineData(10, 7, 10, 7)]
    [InlineData(12, 4, 12, 4)]
    public async Task Projection_UnorderedSnapshotsConvergeToLatestCompleteValue(
        long incomingVersion, int incomingStock, long expectedVersion, int expectedStock)
    {
        var snapshot = new TestValue<StockSnapshot> { Value = new StockSnapshot(10, 7) };
        using var grain = new StockProjectionGrain(Substitute.For<IDurableInbox>(), Serializer, snapshot);
        var update = new StockSnapshot(incomingVersion, incomingStock);
        var attempt = CreateContext(update, HierarchicalKey.Create("stock/widget"));

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
        using var grain = new StockProjectionGrain(Substitute.For<IDurableInbox>(), Serializer, snapshot);
        var update = new StockSnapshot(10, 99);
        var attempt = CreateContext(update, HierarchicalKey.Create("stock/widget"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(new StockSnapshot(10, 7), snapshot.Value);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task OrderDispatcher_MultipleKindsAndFreshDuplicatesRecordOneOutcomePerStep()
    {
        var ledger = new TestDictionary<HierarchicalKey, OrderOutcome>();
        var inbox = Substitute.For<IDurableInbox>();
        using var grain = new OrderOutcomesGrain(inbox, Serializer, ledger);
        var reservation = new ReservationResult(HierarchicalKey.Create("orders/42/inventory/reserve"), 3, true);
        var charge = new ChargePayment(HierarchicalKey.Create("orders/42/payment/charge"), 12.5m, "USD", Sender);
        var payment = new PaymentResult(charge, "provider-charge-1", true);
        var first = CreateContext(reservation, reservation.OperationKey);
        var second = CreateContext(payment, charge.OperationKey);
        var duplicate = CreateContext(reservation, reservation.OperationKey);

        foreach (var attempt in new[] { first, second, duplicate })
        {
            var handling = grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
            Assert.True(handling.IsCompletedSuccessfully);
            await handling;
            Assert.Equal(new[] { "complete" }, attempt.Events);
            Assert.Empty(attempt.Output);
        }
        Assert.Equal(2, await grain.GetCompletedStepCountAsync());
        Assert.Equal(reservation, ledger[reservation.OperationKey]);
        Assert.Equal(payment, ledger[charge.OperationKey]);
        inbox.Received(1).RegisterHandler(grain);
    }

    [Fact]
    public async Task OrderDispatcher_ConflictingOutcomePreservesRecordedStep()
    {
        var key = HierarchicalKey.Create("orders/42/inventory/reserve");
        var original = new ReservationResult(key, 3, true);
        var ledger = new TestDictionary<HierarchicalKey, OrderOutcome> { [key] = original };
        using var grain = new OrderOutcomesGrain(Substitute.For<IDurableInbox>(), Serializer, ledger);
        var attempt = CreateContext(original with { Reserved = false }, key);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(original, Assert.Single(ledger).Value);
        Assert.Empty(attempt.Events);
        Assert.Empty(attempt.Output);
    }

    [Fact]
    public async Task Campaign_FanoutStagesFrozenRecipientIntentsBeforeAwaitingAcknowledgement()
    {
        var campaigns = new TestDictionary<Guid, NotificationCampaign>();
        var outbox = Substitute.For<IDurableOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(Sender);
        using var grain = new CampaignGrain(outbox, state, campaigns, Serializer, grainContext);
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
        var root = HierarchicalKey.Create("campaigns").CreateChildKey(id.ToString("N"));
        for (var index = 0; index < outputs.Count; index++)
        {
            var envelope = outputs[index];
            Assert.Equal(Sender, envelope.SenderId);
            var payload = ReadBody<Notify>(envelope);
            Assert.Equal("campaign text", payload.Text);
            Assert.Equal(root.CreateChildKey(Uri.EscapeDataString(original[index].ToString())), payload.OperationKey);
            Assert.Null(payload.ResponseDestination);
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
        using var grain = new CampaignGrain(outbox, state, campaigns, Serializer, context);

        await Assert.ThrowsAsync<ArgumentException>(() => grain.PublishAsync(id,
            changeRecipients ? "original" : "changed",
            changeRecipients ? [GrainId.Create("notification", "bob")] : original));

        Assert.Same(campaign, Assert.Single(campaigns).Value);
        Assert.Equal(original, campaign.Recipients);
        outbox.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    private readonly IDurableOutbox Outbox = Substitute.For<IDurableOutbox>();
    private readonly Dictionary<Guid, (List<DurableEnvelope> Output, List<string> Events)> _attempts = [];
    private Guid _activeAttempt;

    private (IInboxHandlerContext Context, List<DurableEnvelope> Output, List<string> Events) CreateContext<T>(
        T body, HierarchicalKey key) where T : class
    {
        // key is business protocol data, never transport metadata.
        ArgumentNullException.ThrowIfNull(key);
        var envelope = Own(_payload.Envelope(Sender, Receiver, body));
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(_ =>
        {
            _activeAttempt = envelope.MessageId;
            return envelope;
        });
        var output = new List<DurableEnvelope>();
        var events = new List<string>();
        _attempts.Add(envelope.MessageId, (output, events));
        if (_attempts.Count == 1)
        {
            Outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
            {
                var active = _attempts[_activeAttempt];
                active.Output.Add(Own(call.Arg<DurableEnvelope>().Retain()));
                active.Events.Add("send");
            });
        }
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        return (context, output, events);
    }

    private T ReadBody<T>(DurableEnvelope envelope) where T : class =>
        _payload.Decode<T>(envelope.Payload);

    public void Dispose()
    {
        foreach (var envelope in _owned) envelope.Dispose();
        _payload.Dispose();
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
