using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OrderProcessing;
using OrderProcessing.Grains;
using Orleans.Configuration;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;
using StockSample = global::Messaging;

namespace Orleans.Messaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class OrderProcessingContractTests
{
    [Fact]
    public void PositionalContracts_PinExistingConstructorFieldOrdinals()
    {
        Type[] contracts =
        [
            typeof(Address), typeof(OrderLine), typeof(CatalogItem), typeof(BasketItem), typeof(CheckoutRequest),
            typeof(OrderState), typeof(StockLine), typeof(OrderDetails), typeof(StockReservation), typeof(SubmitOrder),
            typeof(GracePeriodElapsed), typeof(OrderSummary), typeof(ReserveStock), typeof(StockResult),
            typeof(ReleaseStock), typeof(CommitStock), typeof(AuthorizePayment), typeof(PaymentResult)
        ];

        foreach (var contract in contracts)
        {
            var constructor = Assert.Single(contract.GetConstructors());
            var parameters = constructor.GetParameters();
            for (var index = 0; index < parameters.Length; index++)
            {
                Assert.Equal((uint)index, Assert.Single(parameters[index].GetCustomAttributes<IdAttribute>()).Id);
                Assert.Empty(contract.GetProperty(parameters[index].Name!)!.GetCustomAttributes<IdAttribute>());
            }
        }
    }

    [Fact]
    public void ExplicitIds_PreserveTheExistingConstructorWireSection()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var state = new OrderState(Guid.NewGuid(), "buyer", 7, OrderStatus.Paid, DateTimeOffset.UnixEpoch,
            [new(1, "boots", 12.5m, 2)], new("street", "city", "state", "country", "zip"), "1234", "paid");
        var legacy = new PositionalOrderState(state.OrderId, state.BuyerId, state.Version, state.Status,
            state.OrderDate, state.Lines, state.Address, state.CardLast4, state.Description);
        var bytes = serializer.SerializeToArray(legacy);

        Assert.Equal(bytes, serializer.SerializeToArray(state));
        var recovered = Assert.IsType<OrderState>(serializer.Deserialize<OrderState>(bytes));
        Assert.Equal(state.ToSummary(), recovered.ToSummary());
        Assert.Equal(state.Lines, recovered.Lines);
        Assert.Equal(state.Address, recovered.Address);
    }

    [Fact]
    public void SampleOutcomeIds_RoundTripWithinTheirOwnInheritanceLayers()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<StockSample.ReservationOutcome>>();
        var command = MessageId.Create("orders", "wire-contract", "reserve");
        StockSample.ReservationOutcome[] values =
        [
            new StockSample.ReservationAccepted(command, 2, 8),
            new StockSample.ReservationRejected(command, 12, 10, StockSample.ReservationRejectionReason.InsufficientStock)
        ];

        foreach (var value in values)
        {
            Assert.Equal(value, serializer.Deserialize(serializer.SerializeToArray(value)));
        }

        Assert.Equal(0U, typeof(StockSample.ReservationOutcome).GetProperty("CommandId")!
            .GetCustomAttribute<IdAttribute>()!.Id);
        Assert.Equal(0U, typeof(StockSample.ReservationRejected).GetProperty("Reason")!
            .GetCustomAttribute<IdAttribute>()!.Id);
    }

    [GenerateSerializer, Immutable]
    public sealed record PositionalOrderState(
        Guid OrderId, string BuyerId, int Version, OrderStatus Status, DateTimeOffset OrderDate,
        OrderLine[] Lines, Address Address, string CardLast4, string? Description);

    [GenerateSerializer, Immutable]
    public sealed record PositionalSubmitOrder(string BuyerId, OrderLine[] Lines, Address Address, string CardLast4);
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class OrderProcessingSampleTests : IAsyncLifetime
{
    private readonly SampleFixture _fixture = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private OrderMessages Messages => _fixture.Cluster.Silos[0].ServiceProvider.GetRequiredService<OrderMessages>();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();
    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task LegacySubmission_LaterSendFailureRetainsItsTimestampAndEnvelopesAcrossReactivation()
    {
        var order = _fixture.Client.GetGrain<IOrderGrain>(Guid.NewGuid());
        Assert.Null(await order.GetAsync());
        var serializer = _fixture.Client.ServiceProvider.GetRequiredService<Serializer>();
        var legacy = new OrderProcessingContractTests.PositionalSubmitOrder("buyer", [new(1, "boots", 12.5m, 2)],
            new("street", "city", "state", "country", "zip"), "1234");
        var bytes = serializer.SerializeToArray(legacy);
        Assert.Equal(default, Assert.IsType<SubmitOrder>(serializer.Deserialize<SubmitOrder>(bytes)).OrderDate);
        var attempt = Substitute.For<IInboxHandlerContext>();
        attempt.Message.Returns(new InboxMessage
        {
            Envelope = new()
            {
                MessageId = MessageId.Create("sample-test", "undated"),
                Subject = Messages.SubmitOrder.Subject,
                Payload = bytes
            }
        });
        var outbox = Outbox(order);
        outbox.FailSubject = Messages.GracePeriodElapsed.Subject;
        var capturedAt = _fixture.Clock.GetUtcNow();

        await Assert.ThrowsAsync<IOException>(() => HandleAsync(order, attempt));
        var prepared = outbox.Attempts.ToArray();
        Assert.Equal(2, prepared.Length);
        Assert.Null(await order.GetAsync());
        attempt.DidNotReceive().Complete();
        await DeactivateAsync(order);
        _fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await order.GetAsync());

        await HandleAsync(order, attempt);

        var submitted = (await order.GetAsync())!.Order;
        Assert.Equal(OrderStatus.Submitted, submitted.Status);
        Assert.Equal(capturedAt, submitted.OrderDate);
        var retried = Outbox(order).Attempts.ToArray();
        Assert.Equal(prepared.Length, retried.Length);
        for (var index = 0; index < prepared.Length; index++)
        {
            Assert.True(EnvelopeEquivalence.AreEquivalent(prepared[index], retried[index]));
        }
        Assert.Equal(capturedAt + TimeSpan.FromSeconds(10), retried[1].Envelope.DeliverAt);
        attempt.Received(1).Complete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submission_SendFailurePreservesStateAndStableRetryEnvelopes(bool failScheduledMessage)
    {
        var order = _fixture.Client.GetGrain<IOrderGrain>(Guid.NewGuid());
        Assert.Null(await order.GetAsync());
        var outbox = Outbox(order);
        var state = await _fixture.OnGrainAsync(order, () => State(order).GetOrAddValue<OrderState>("order"));
        var request = new SubmitOrder("buyer", [new(1, "boots", 12.5m, 2)],
            new("street", "city", "state", "country", "zip"), "1234", _fixture.Clock.GetUtcNow());
        var attempt = Context(order, Messages.SubmitOrder, request);
        outbox.FailSubject = failScheduledMessage ? Messages.GracePeriodElapsed.Subject : Messages.StatusChanged.Subject;
        outbox.BeforeSend = () => Assert.Null(state.Value);

        await Assert.ThrowsAsync<IOException>(() => HandleAsync(order, attempt));

        Assert.Null(await order.GetAsync());
        attempt.DidNotReceive().Complete();
        await HandleAsync(order, attempt);
        var submitted = (await order.GetAsync())!.Order;
        Assert.Equal(OrderStatus.Submitted, submitted.Status);
        Assert.Equal(request.OrderDate, submitted.OrderDate);
        Assert.Equal(0, submitted.Version);
        Assert.Equal(2, outbox.Messages.Count);
        Assert.Equal(request.OrderDate + TimeSpan.FromSeconds(10),
            Assert.Single(outbox.Messages.Values, message => message.Envelope.Subject == Messages.GracePeriodElapsed.Subject)
                .Envelope.DeliverAt);
        attempt.Received(1).Complete();
    }

    [Theory]
    [InlineData("grace", OrderStatus.Submitted, OrderStatus.AwaitingValidation)]
    [InlineData("confirm", OrderStatus.AwaitingValidation, OrderStatus.StockConfirmed)]
    [InlineData("reject", OrderStatus.AwaitingValidation, OrderStatus.Cancelled)]
    [InlineData("paid", OrderStatus.StockConfirmed, OrderStatus.Paid)]
    [InlineData("declined", OrderStatus.StockConfirmed, OrderStatus.Cancelled)]
    [InlineData("cancel", OrderStatus.AwaitingValidation, OrderStatus.Cancelled)]
    [InlineData("ship", OrderStatus.Paid, OrderStatus.Shipped)]
    public async Task Transition_SendFailurePreservesOrderAndStockUntilRetry(
        string operation, OrderStatus initialStatus, OrderStatus expectedStatus)
    {
        var orderId = Guid.NewGuid();
        var order = _fixture.Client.GetGrain<IOrderGrain>(orderId);
        Assert.Null(await order.GetAsync());
        var state = await _fixture.OnGrainAsync(order, () => State(order).GetOrAddValue<OrderState>("order"));
        var stock = await _fixture.OnGrainAsync(order, () => State(order).GetOrAddDictionary<int, StockLineStatus>("stock"));
        var original = new OrderState(orderId, "buyer", 5, initialStatus, _fixture.Clock.GetUtcNow(),
            [new(1, "boots", 12.5m, 2), new(2, "harness", 8m, 1)],
            new("street", "city", "state", "country", "zip"), "1234", null);
        await _fixture.OnGrainAsync(order, () =>
        {
            state.Value = original;
            if (operation != "grace")
            {
                stock[1] = operation == "ship" ? StockLineStatus.Committed : StockLineStatus.Confirmed;
                stock[2] = operation is "confirm" or "reject" ? StockLineStatus.Pending : stock[1];
            }
        });
        var previousStock = stock.OrderBy(line => line.Key).ToArray();
        var outbox = Outbox(order);
        outbox.FailSubject = Messages.StatusChanged.Subject;
        outbox.BeforeSend = () =>
        {
            Assert.Equal(original, state.Value);
            Assert.Equal(previousStock, stock.OrderBy(line => line.Key).ToArray());
        };
        var attempt = operation switch
        {
            "grace" => Context(order, Messages.GracePeriodElapsed, new GracePeriodElapsed()),
            "confirm" or "reject" => Context(order, Messages.StockResult, new StockResult(2, operation == "confirm")),
            "paid" or "declined" => Context(order, Messages.PaymentResult, new PaymentResult(operation == "paid", "declined")),
            _ => null
        };
        Func<Task> act = operation switch
        {
            "cancel" => async () => Assert.True(await order.CancelAsync()),
            "ship" => async () => Assert.True(await order.ShipAsync()),
            _ => () => HandleAsync(order, attempt!)
        };

        await Assert.ThrowsAsync<IOException>(act);

        var unchanged = (await order.GetAsync())!.Order;
        Assert.Equal(original.Lines, unchanged.Lines);
        Assert.Equal(original with { Lines = unchanged.Lines }, unchanged);
        Assert.Equal(previousStock, stock.OrderBy(line => line.Key).ToArray());
        attempt?.DidNotReceive().Complete();
        await act();
        var completed = (await order.GetAsync())!;
        Assert.Equal(expectedStatus, completed.Order.Status);
        Assert.Equal(original.Version + 1, completed.Order.Version);
        var expectedStock = operation switch
        {
            "grace" => new[] { StockLineStatus.Pending, StockLineStatus.Pending },
            "confirm" => [StockLineStatus.Confirmed, StockLineStatus.Confirmed],
            "reject" => [StockLineStatus.Released, StockLineStatus.Rejected],
            "paid" or "ship" => [StockLineStatus.Committed, StockLineStatus.Committed],
            _ => [StockLineStatus.Released, StockLineStatus.Released]
        };
        Assert.Equal(expectedStock, completed.Stock.OrderBy(line => line.ProductId).Select(line => line.Status));
        attempt?.Received(1).Complete();
    }

    [Fact]
    public async Task ReleaseBeforeReserve_RetainsItsRejectionAcrossReactivationAndNewMessageIds()
    {
        var catalog = _fixture.Client.GetGrain<ICatalogItemGrain>(1);
        var initial = await catalog.GetAsync();
        var orderId = Guid.NewGuid();
        await HandleAsync(catalog, Context(catalog, Messages.ReleaseStock, new ReleaseStock(orderId)));
        await DeactivateAsync(catalog);
        _ = await catalog.GetAsync();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await HandleAsync(catalog, Context(catalog, Messages.ReserveStock,
                new ReserveStock(orderId, 2, GrainId.Create("test-order", "reply"))));
            Assert.Equal(initial, await catalog.GetAsync());
            Assert.All(Outbox(catalog).Messages.Values, message =>
                Assert.False(Read<StockResult>(message).Confirmed));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishedReservation_RetainsItsOutcomeWithoutChangingStockAgain(bool commit)
    {
        var catalog = _fixture.Client.GetGrain<ICatalogItemGrain>(1);
        var initial = (await catalog.GetAsync())!;
        var orderId = Guid.NewGuid();
        var reserve = new ReserveStock(orderId, 2, GrainId.Create("test-order", "reply"));
        await HandleAsync(catalog, Context(catalog, Messages.ReserveStock, reserve));
        Assert.True(Read<StockResult>(Assert.Single(Outbox(catalog).Messages).Value).Confirmed);
        await HandleAsync(catalog, commit
            ? Context(catalog, Messages.CommitStock, new CommitStock(orderId))
            : Context(catalog, Messages.ReleaseStock, new ReleaseStock(orderId)));
        var finished = await catalog.GetAsync();
        Assert.Equal(commit ? initial with { AvailableStock = initial.AvailableStock - 2, SoldStock = 2 } : initial, finished);
        await DeactivateAsync(catalog);
        _ = await catalog.GetAsync();

        await HandleAsync(catalog, Context(catalog, Messages.ReserveStock, reserve));
        Assert.True(Read<StockResult>(Assert.Single(Outbox(catalog).Messages).Value).Confirmed);
        await HandleAsync(catalog, Context(catalog, Messages.ReleaseStock, new ReleaseStock(orderId)));
        await HandleAsync(catalog, Context(catalog, Messages.CommitStock, new CommitStock(orderId)));
        Assert.Equal(finished, await catalog.GetAsync());

        var conflict = Context(catalog, Messages.ReserveStock, reserve with { Units = 3 });
        var outputCount = Outbox(catalog).Messages.Count;
        await HandleAsync(catalog, conflict);
        conflict.Received(1).Fail($"Order {orderId} already requested a different reservation quantity.");
        Assert.Equal(outputCount, Outbox(catalog).Messages.Count);
        Assert.Equal(finished, await catalog.GetAsync());
    }

    [Fact]
    public async Task RejectedReservation_KeepsItsOutcomeAfterStockBecomesAvailable()
    {
        var catalog = _fixture.Client.GetGrain<ICatalogItemGrain>(1);
        var initial = (await catalog.GetAsync())!;
        var replyTo = GrainId.Create("test-order", "reply");
        var holdingOrder = Guid.NewGuid();
        var rejected = new ReserveStock(Guid.NewGuid(), 1, replyTo);
        await HandleAsync(catalog, Context(catalog, Messages.ReserveStock,
            new ReserveStock(holdingOrder, initial.AvailableStock, replyTo)));
        await HandleAsync(catalog, Context(catalog, Messages.ReserveStock, rejected));
        await HandleAsync(catalog, Context(catalog, Messages.ReleaseStock, new ReleaseStock(holdingOrder)));
        await DeactivateAsync(catalog);
        _ = await catalog.GetAsync();

        await HandleAsync(catalog, Context(catalog, Messages.ReserveStock, rejected));

        Assert.False(Read<StockResult>(Assert.Single(Outbox(catalog).Messages).Value).Confirmed);
        Assert.Equal(initial, await catalog.GetAsync());
    }

    private T Read<T>(OutboxMessage message) =>
        Assert.IsType<T>(_fixture.Client.ServiceProvider.GetRequiredService<Serializer<T>>().Deserialize(message.Envelope.Payload));

    private IInboxHandlerContext Context<T>(IAddressable grain, MessageType<T> type, T body)
    {
        var context = Substitute.For<IInboxHandlerContext>();
        context.Message.Returns(type.Create(MessageId.Create("sample-test", Guid.NewGuid().ToString("N")),
            grain.GetGrainId(), body).ToInboxMessage());
        return context;
    }

    private async Task HandleAsync(IAddressable grain, IInboxHandlerContext context) =>
        await await _fixture.OnGrainAsync(grain, () =>
            ((TestInbox)Services(grain).GetRequiredService<IInbox>()).HandleAsync(context, Token).AsTask());

    private async Task DeactivateAsync(IAddressable grain)
    {
        await await _fixture.OnGrainAsync(grain, () => State(grain).WriteStateAsync(Token).AsTask());
        var context = _fixture.GetGrainContext(grain);
        await _fixture.OnGrainAsync(grain, () => Services(grain).GetRequiredService<IGrainRuntime>().DeactivateOnIdle(context));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    private IServiceProvider Services(IAddressable grain) => _fixture.GetGrainContext(grain).ActivationServices;
    private IDurableStateManager State(IAddressable grain) => Services(grain).GetRequiredService<IDurableStateManager>();
    private RecordingOutbox Outbox(IAddressable grain) => Services(grain).GetRequiredService<RecordingOutbox>();

    private sealed class SampleFixture() : MessagingClusterFixture(1, receiverOnly: false)
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            services.PostConfigure<GrainTypeOptions>(options =>
            {
                options.Classes.Remove(typeof(global::Documentation.Grains.Messaging.PaymentGrain));
                options.AddClass(typeof(OrderProcessing.Grains.PaymentGrain));
            });
            services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "json");
            services.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(OrderProcessingJsonContext.Default));
            services.AddSingleton<OrderMessages>();
            services.AddScoped(_ => new TestInbox());
            services.Replace(ServiceDescriptor.Scoped<IInbox>(sp => sp.GetRequiredService<TestInbox>()));
            services.AddScoped(_ => new RecordingOutbox());
            services.Replace(ServiceDescriptor.Scoped<IOutbox>(sp => sp.GetRequiredService<RecordingOutbox>()));
        }
    }

    private sealed class RecordingOutbox : IOutbox
    {
        public Dictionary<MessageId, OutboxMessage> Messages { get; } = [];
        public List<OutboxMessage> Attempts { get; } = [];
        public string? FailSubject { get; set; }
        public Action? BeforeSend { get; set; }

        public void Send(OutboxMessage message)
        {
            Attempts.Add(message);
            BeforeSend?.Invoke();
            if (message.Envelope.Subject == FailSubject)
            {
                FailSubject = null;
                throw new IOException("Injected outgoing message staging failure.");
            }

            if (Messages.TryGetValue(message.Envelope.MessageId, out var previous))
            {
                Assert.True(EnvelopeEquivalence.AreEquivalent(previous, message), "A retry must stage the same envelope.");
                return;
            }

            Messages.Add(message.Envelope.MessageId, message);
        }

        public void Send(MessageId messageId) => throw new NotSupportedException();
        public bool Withdraw(MessageId messageId) => throw new NotSupportedException();
        public IOutbox Register(IOutboxMessageSource source) => throw new NotSupportedException();
    }
}
