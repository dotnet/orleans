using Documentation.Grains.DurableMessaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
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
        var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), Substitute.For<IDurableStateManager>(), stock, ledger);
        var request = new ReserveStock(HierarchicalKey.Create("orders/42/inventory/widget/reserve"), quantity);
        var first = CreateContext(request, request.OperationKey);
        var second = CreateContext(request, request.OperationKey);
        Assert.NotEqual(first.Context.Envelope.MessageId, second.Context.Envelope.MessageId);

        await grain.HandleAsync(request, first.Context, TestContext.Current.CancellationToken);
        Assert.Equal(expectedStock, stock.Value);
        stock.Value = expectedStock + 100;
        await grain.HandleAsync(request, second.Context, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStock + 100, stock.Value);
        var outcome = Assert.Single(ledger).Value;
        Assert.Equal(new ReservationResult(request.OperationKey, quantity, reserved), outcome);
        Assert.Equal(outcome, ReadBody<ReservationResult>(Assert.Single(first.Output)));
        Assert.Equal(outcome, ReadBody<ReservationResult>(Assert.Single(second.Output)));
        Assert.Equal(new[] { "send", "complete" }, first.Events);
        Assert.Equal(new[] { "send", "complete" }, second.Events);
        Assert.Equal(Sender, second.Output[0].ReceiverId);
        Assert.Equal(request.OperationKey, second.Output[0].CorrelationKey);
    }

    [Fact]
    public async Task Reservation_ConflictingKeyReusePreservesStockAndOutcome()
    {
        var key = HierarchicalKey.Create("orders/42/inventory/widget/reserve");
        var original = new ReservationResult(key, 3, true);
        var stock = new TestValue<int> { Value = 7 };
        var ledger = new TestDictionary<HierarchicalKey, ReservationResult> { [key] = original };
        var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), Substitute.For<IDurableStateManager>(), stock, ledger);
        var request = new ReserveStock(key, 4);
        var attempt = CreateContext(request, key);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(request, attempt.Context, TestContext.Current.CancellationToken));

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
        var grain = new InventoryGrain(Substitute.For<IDurableInbox>(), Substitute.For<IDurableStateManager>(), stock, ledger);
        var request = new ReserveStock(HierarchicalKey.Create("orders/42/inventory/widget/reserve"), 3);
        var attempt = CreateContext(request, request.OperationKey);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await grain.HandleAsync(request, attempt.Context, cancellation.Token));

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
        var grain = new PaymentGrain(Substitute.For<IDurableInbox>(), gateway, ledger);
        var request = new ChargePayment(HierarchicalKey.Create("tenants/acme/orders/42/payment/charge"), 12.5m, "USD");
        var first = CreateContext(request, request.OperationKey);
        if (cancelAfterProviderSuccess)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await grain.HandleAsync(request, first.Context, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await grain.HandleAsync(request, first.Context, cancellation.Token));
        }
        Assert.Empty(ledger);
        Assert.Empty(first.Output);
        Assert.Empty(first.Events);
        Assert.Equal(1, gateway.Charges);

        var retry = CreateContext(request, request.OperationKey);
        await grain.HandleAsync(request, retry.Context, TestContext.Current.CancellationToken);
        var duplicate = CreateContext(request, request.OperationKey);
        await grain.HandleAsync(request, duplicate.Context, TestContext.Current.CancellationToken);

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
    public async Task Payment_ConflictingKeyReusePreservesOriginalProviderOutcome()
    {
        var ledger = new TestDictionary<HierarchicalKey, PaymentResult>();
        var gateway = new IdempotentGateway();
        var grain = new PaymentGrain(Substitute.For<IDurableInbox>(), gateway, ledger);
        var original = new ChargePayment(HierarchicalKey.Create("orders/42/payment/charge"), 12.5m, "USD");
        var first = CreateContext(original, original.OperationKey);
        await grain.HandleAsync(original, first.Context, TestContext.Current.CancellationToken);
        var conflicting = original with { Amount = 25m };
        var attempt = CreateContext(conflicting, conflicting.OperationKey);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(conflicting, attempt.Context, TestContext.Current.CancellationToken));

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
        var grain = new StockProjectionGrain(Substitute.For<IDurableInbox>(), snapshot);
        var update = new StockSnapshot(incomingVersion, incomingStock);
        var attempt = CreateContext(update, HierarchicalKey.Create("stock/widget"));

        var handling = grain.HandleAsync(update, attempt.Context, TestContext.Current.CancellationToken);

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
        var grain = new StockProjectionGrain(Substitute.For<IDurableInbox>(), snapshot);
        var update = new StockSnapshot(10, 99);
        var attempt = CreateContext(update, HierarchicalKey.Create("stock/widget"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await grain.HandleAsync(update, attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(new StockSnapshot(10, 7), snapshot.Value);
        Assert.Empty(attempt.Events);
    }

    private (IInboxHandlerContext Context, List<DurableEnvelope> Output, List<string> Events) CreateContext<T>(
        T body, HierarchicalKey key)
    {
        var sessions = _services.GetRequiredService<SerializerSessionPool>();
        var envelope = new DurableEnvelopeBuilder(sessions, Sender)
            .To(Receiver, "recipe")
            .WithReplyTo(Sender)
            .WithCorrelationKey(key)
            .WithBody(body)
            .Build();
        var context = Substitute.For<IInboxHandlerContext>();
        context.Envelope.Returns(envelope);
        context.CreateEnvelope().Returns(_ => new DurableEnvelopeBuilder(sessions, Receiver));
        var output = new List<DurableEnvelope>();
        var events = new List<string>();
        context.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            output.Add(call.Arg<DurableEnvelope>());
            events.Add("send");
        });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        return (context, output, events);
    }

    private static T ReadBody<T>(DurableEnvelope envelope)
    {
        Assert.True(envelope.Data.TryGetBody<T>(out var result));
        return result!;
    }

    public void Dispose() => _services.Dispose();

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
