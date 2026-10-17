using Documentation.Grains.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Messaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingRecipeTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly GrainId Sender = GrainId.Create("order", "42");
    private static readonly GrainId Receiver = GrainId.Create("inventory", "widget");
    private static readonly MessageId Command = MessageId.Create("orders", "42", "reserve");
    private readonly IOutbox _outbox = Substitute.For<IOutbox>();
    private readonly Messages _messages;
    private (List<OutboxMessage> Output, List<string> Events) _activeAttempt;

    public MessagingRecipeTests()
    {
        _messages = new(_services.GetRequiredService<Serializer>());
        _outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call =>
        {
            _activeAttempt.Output.Add(call.Arg<OutboxMessage>());
            _activeAttempt.Events.Add("send");
        });
    }

    private MessageType<T> Type<T>() => typeof(Messages).GetProperties()
        .Select(property => property.GetValue(_messages)).OfType<MessageType<T>>().SingleOrDefault()
        ?? new(typeof(T).Name, _services.GetRequiredService<Serializer<T>>());

    private static async Task<(TGrain Grain, TestInbox Handler)> RegisterAsync<TGrain>(Func<IInbox, TGrain> factory)
        where TGrain : Grain
    {
        var handler = new TestInbox();
        var grain = factory(handler);
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);
        return (grain, handler);
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
        Assert.True(order.IsAncestorOf(reservation.Key));
        Assert.True(order.IsAncestorOf(OrderOperationKeys.Charge(order).Key));
        Assert.True(reservation != OrderOperationKeys.Charge(order));
        Assert.False(OrderOperationKeys.Order("acme/us", orderId).IsAncestorOf(reservation.Key));
        Assert.False(HierarchicalKey.Create("orders", "42").IsAncestorOf(HierarchicalKey.Create("orders", "420", "payment")));
        var escaped = HierarchyExample.Create();
        Assert.Equal(@"orders/42/inventory/widget\/blue/reserve", escaped.Step.ToString());
        Assert.True(escaped.Order.IsAncestorOf(escaped.Step));
        Assert.Equal(escaped.Step, HierarchicalKey.Parse(escaped.Step.ToString(), null));
    }

    [Theory]
    [InlineData(10, 3, 7)]
    [InlineData(3, 3, 0)]
    public async Task Reservation_AcceptedCommandStagesTypedResultBeforeStockMutation(
        int available, int quantity, int expectedStock)
    {
        var stock = new TestValue<int> { Value = available };
        var (_, handler) = await CreateInventoryAsync(stock);
        var request = new ReserveStock(quantity, Sender);
        var attempt = CreateContext(request, Command);
        _outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(_ => Assert.Equal(available, stock.Value));

        var handling = handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        await handling;
        Assert.Equal(expectedStock, stock.Value);
        var reply = Assert.Single(attempt.Output);
        Assert.Equal(new MessageId(Command.Key.CreateChildKey("result")), reply.Envelope.MessageId);
        Assert.Equal(Sender, reply.ReceiverId);
        Assert.Equal(new ReservationAccepted(quantity, expectedStock),
            Assert.IsType<ReservationAccepted>(ReadBody<ReservationResult>(reply)));
        Assert.Equal(new[] { "send", "complete" }, attempt.Events);
    }

    [Theory]
    [InlineData(10, 0, ReservationRejectionReason.InvalidQuantity)]
    [InlineData(10, -1, ReservationRejectionReason.InvalidQuantity)]
    [InlineData(2, 3, ReservationRejectionReason.InsufficientStock)]
    [InlineData(0, 1, ReservationRejectionReason.InsufficientStock)]
    public async Task Reservation_RejectedCommandStagesReasonAndCompletesWithoutStockMutation(
        int available, int quantity, ReservationRejectionReason reason)
    {
        var stock = new TestValue<int> { Value = available };
        var (_, handler) = await CreateInventoryAsync(stock);
        var attempt = CreateContext(new ReserveStock(quantity, Sender), Command);

        var handling = handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        await handling;
        Assert.Equal(available, stock.Value);
        var reply = Assert.Single(attempt.Output);
        Assert.Equal(new MessageId(Command.Key.CreateChildKey("result")), reply.Envelope.MessageId);
        Assert.Equal(Sender, reply.ReceiverId);
        Assert.Equal(new ReservationRejected(quantity, available, reason),
            Assert.IsType<ReservationRejected>(ReadBody<ReservationResult>(reply)));
        Assert.Equal(new[] { "send", "complete" }, attempt.Events);
        attempt.Context.DidNotReceive().Fail(Arg.Any<string>());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Reservation_ReplySendFailurePreservesStockAndCompletion(int quantity)
    {
        var stock = new TestValue<int> { Value = 10 };
        var outbox = Substitute.For<IOutbox>();
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(_ =>
        {
            Assert.Equal(10, stock.Value);
            throw new IOException("The reply could not be staged.");
        });
        var (_, handler) = await RegisterAsync(inbox => new InventoryGrain(inbox, outbox, _messages,
            Substitute.For<IDurableStateManager>(), stock));
        var attempt = CreateContext(new ReserveStock(quantity, Sender), Command);

        await Assert.ThrowsAsync<IOException>(async () =>
            await handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(10, stock.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
        outbox.Received(1).Send(Arg.Any<OutboxMessage>());
    }

    [Fact]
    public void ReservationResults_PolymorphicRoundTripsPreserveBothOutcomeTypesAndRejectionReasons()
    {
        ReservationResult[] outcomes =
        [
            new ReservationAccepted(3, 7),
            new ReservationRejected(0, 10, ReservationRejectionReason.InvalidQuantity),
            new ReservationRejected(-1, 10, ReservationRejectionReason.InvalidQuantity),
            new ReservationRejected(11, 10, ReservationRejectionReason.InsufficientStock)
        ];
        foreach (var outcome in outcomes)
        {
            var reservation = Type<ReservationResult>().Create(Command, Receiver, outcome);
            var reservationCopy = ReadBody<ReservationResult>(reservation);
            Assert.Equal(outcome.GetType(), reservationCopy.GetType());
            Assert.Equal(outcome, reservationCopy);

            var order = Type<OrderOutcome>().Create(Command, Receiver, outcome);
            var orderCopy = ReadBody<OrderOutcome>(order);
            Assert.Equal(outcome.GetType(), orderCopy.GetType());
            Assert.Equal(outcome, orderCopy);
        }
    }

    [Fact]
    public async Task Reservation_LocalCancellationPreservesStockAndCompletion()
    {
        var stock = new TestValue<int> { Value = 10 };
        var (_, handler) = await CreateInventoryAsync(stock);
        var attempt = CreateContext(new ReserveStock(3, Sender), Command);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await handler.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(10, stock.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task InventorySubjects_RestockAndReservationSelectTheirTypedMethods()
    {
        var stock = new TestValue<int> { Value = 2 };
        var (grain, handler) = await CreateInventoryAsync(stock);
        var restock = CreateContext(new Restock(5), MessageId.Create("stock", "restock"));
        await handler.HandleAsync(restock.Context, TestContext.Current.CancellationToken);
        Assert.Equal(7, await grain.GetAvailableAsync());
        Assert.Equal(new[] { "complete" }, restock.Events);
        Assert.Empty(restock.Output);

        var reserve = CreateContext(new ReserveStock(3, Sender), Command);
        await handler.HandleAsync(reserve.Context, TestContext.Current.CancellationToken);

        Assert.Equal(4, await grain.GetAvailableAsync());
        Assert.Equal(new ReservationAccepted(3, 4),
            Assert.IsType<ReservationAccepted>(ReadBody<ReservationResult>(Assert.Single(reserve.Output))));
        Assert.Equal(new[] { "send", "complete" }, reserve.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Restock_InvalidQuantityDeadLettersWithoutStockMutationOrReply(int quantity)
    {
        var stock = new TestValue<int> { Value = 10 };
        var (_, handler) = await CreateInventoryAsync(stock);
        var attempt = CreateContext(new Restock(quantity), Command);

        var handling = handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        await handling;
        Assert.Equal(10, stock.Value);
        Assert.Empty(attempt.Output);
        Assert.Equal(new[] { "dead-letter" }, attempt.Events);
        attempt.Context.Received(1).Fail("Restock quantity must be positive.");
        attempt.Context.DidNotReceive().Complete();
    }

    [Fact]
    public async Task Restock_CheckedOverflowPreservesStockAndCompletionForRetry()
    {
        var stock = new TestValue<int> { Value = 10 };
        var (_, handler) = await CreateInventoryAsync(stock);
        var attempt = CreateContext(new Restock(int.MaxValue), Command);

        await Assert.ThrowsAsync<OverflowException>(async () =>
            await handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(10, stock.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task Restock_PreMutationCancellationLeavesStockAndCompletionUnchanged()
    {
        var stock = new TestValue<int> { Value = 10 };
        var (_, handler) = await CreateInventoryAsync(stock);
        var attempt = CreateContext(new Restock(5), Command);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await handler.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(10, stock.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    private Task<(InventoryGrain Grain, TestInbox Handler)> CreateInventoryAsync(TestValue<int> stock)
    {
        return RegisterAsync(inbox => new InventoryGrain(inbox, _outbox, _messages,
            Substitute.For<IDurableStateManager>(), stock));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payment_AmbiguousProviderOutcomeRetriesOriginalKeyOnce(bool cancelAfterProviderSuccess)
    {
        var results = new TestDictionary<MessageId, PaymentResult>();
        using var cancellation = new CancellationTokenSource();
        var gateway = new IdempotentGateway
        {
            LoseFirstResponse = !cancelAfterProviderSuccess,
            AfterFirstCharge = cancelAfterProviderSuccess ? cancellation.Cancel : null
        };
        var (grain, handler) = await RegisterAsync(inbox => new PaymentGrain(inbox, _outbox, _messages, gateway, results));
        var request = new ChargePayment(12.5m, "USD", Sender);
        var key = MessageId.Create("tenants", "acme", "orders", "42", "charge");
        var first = CreateContext(request, key);
        if (cancelAfterProviderSuccess)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await handler.HandleAsync(first.Context, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await handler.HandleAsync(first.Context, cancellation.Token));
        }
        Assert.Empty(results);
        Assert.Empty(first.Output);
        Assert.Empty(first.Events);
        Assert.Equal(1, gateway.Charges);

        var retry = CreateContext(request, key);
        await handler.HandleAsync(retry.Context, TestContext.Current.CancellationToken);

        Assert.Equal(key, retry.Context.Message.Envelope.MessageId);
        Assert.Equal(1, gateway.Charges);
        Assert.Equal(new[] { key.ToString(), key.ToString() }, gateway.Calls);
        var outcome = Assert.Single(results).Value;
        Assert.Equal(request, outcome.Request);
        Assert.True(outcome.Charged);
        Assert.Equal("provider-charge-1", outcome.ProviderReference);
        Assert.Equal(outcome, await grain.GetResultAsync(key));
        var reply = Assert.Single(retry.Output);
        Assert.Equal(new MessageId(key.Key.CreateChildKey("result")), reply.Envelope.MessageId);
        Assert.Equal(outcome, ReadBody<PaymentResult>(reply));
        Assert.Equal(new[] { "send", "complete" }, retry.Events);
    }

    [Fact]
    public async Task Payment_PreparationAwaitPreservesInputBytesAndStagesReplyBeforeReturn()
    {
        var results = new TestDictionary<MessageId, PaymentResult>();
        var gateway = Substitute.For<IIdempotentPaymentGateway>();
        var request = new ChargePayment(12.5m, "USD", Sender);
        var outcome = new PaymentResult(request, "provider-charge-1", true);
        var prepared = new TaskCompletionSource<PaymentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ChargeAsync(Command.ToString(), request, Arg.Any<CancellationToken>()).Returns(prepared.Task);
        var attempt = CreateContext(request, Command);
        var inputBytes = attempt.Context.Message.Envelope.Payload.ToArray();
        var (_, handler) = await RegisterAsync(inbox => new PaymentGrain(inbox, _outbox, _messages, gateway, results));

        var handling = handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
        Assert.False(handling.IsCompleted);
        Assert.Empty(results);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
        Assert.Equal(request, ReadBody<ChargePayment>(attempt.Context.Message));
        Assert.Equal(inputBytes, attempt.Context.Message.Envelope.Payload.ToArray());
        prepared.SetResult(outcome);
        await handling;
        Assert.Equal(new[] { "send", "complete" }, attempt.Events);
        Assert.Equal(outcome, ReadBody<PaymentResult>(Assert.Single(attempt.Output)));
        Assert.Equal(outcome, results[Command]);
        Assert.Equal(inputBytes, attempt.Context.Message.Envelope.Payload.ToArray());
        Assert.NotSame(attempt.Context.Message.Envelope.Payload,
            Assert.Single(attempt.Output).Envelope.Payload);
    }

    [Theory]
    [InlineData(9, 99, 10, 7)]
    [InlineData(10, 7, 10, 7)]
    [InlineData(12, 4, 12, 4)]
    public async Task Projection_UnorderedSnapshotsConvergeToLatestCompleteValue(
        long incomingVersion, int incomingStock, long expectedVersion, int expectedStock)
    {
        var snapshot = new TestValue<StockSnapshot> { Value = new StockSnapshot(10, 7) };
        var (grain, handler) = await RegisterAsync(inbox => new StockProjectionGrain(inbox, _messages, snapshot));
        var attempt = CreateContext(new StockSnapshot(incomingVersion, incomingStock), Command);

        var handling = handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

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
        var (_, handler) = await RegisterAsync(inbox => new StockProjectionGrain(inbox, _messages, snapshot));
        var attempt = CreateContext(new StockSnapshot(10, 99), Command);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(new StockSnapshot(10, 7), snapshot.Value);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task OrderDispatcher_MultipleSubjectsStoreAcceptedRejectedAndPaymentOutcomes()
    {
        var outcomes = new TestDictionary<MessageId, OrderOutcome>();
        var dispatcher = new TestInbox();
        var grain = new OrderOutcomesGrain(dispatcher, _messages, outcomes);
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);
        ReservationResult reservation = new ReservationAccepted(3, 7);
        ReservationResult rejection = new ReservationRejected(11, 10, ReservationRejectionReason.InsufficientStock);
        var payment = new PaymentResult(new ChargePayment(12.5m, "USD", Sender), "provider-charge-1", true);
        var reservationId = new MessageId(Command.Key.CreateChildKey("result"));
        var rejectionId = MessageId.Create("orders", "43", "reserve", "result");
        var paymentId = MessageId.Create("orders", "42", "charge", "result");
        var first = CreateContext(reservation, reservationId);
        var second = CreateContext(rejection, rejectionId);
        var third = CreateContext(payment, paymentId);

        foreach (var attempt in new[] { first, second, third })
        {
            var handling = dispatcher.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);
            Assert.True(handling.IsCompletedSuccessfully);
            await handling;
            Assert.Equal(new[] { "complete" }, attempt.Events);
            Assert.Empty(attempt.Output);
        }
        Assert.Equal(3, await grain.GetCompletedStepCountAsync());
        Assert.Equal(reservation, Assert.IsType<ReservationAccepted>(await grain.GetOutcomeAsync(reservationId)));
        Assert.Equal(rejection, Assert.IsType<ReservationRejected>(await grain.GetOutcomeAsync(rejectionId)));
        Assert.Equal(payment, await grain.GetOutcomeAsync(paymentId));
    }

    [Fact]
    public async Task GracePeriod_SubmissionSchedulesSelfAddressedStepThatConfirmsOnlySubmittedCheckouts()
    {
        var status = new TestValue<CheckoutStatus>();
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(Receiver);
        var outputs = new List<OutboxMessage>();
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call => outputs.Add(call.Arg<OutboxMessage>()));
        var inbox = new TestInbox();
        var grain = new CheckoutGrain(inbox, outbox, state, _messages, status, grainContext);
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);
        var before = DateTimeOffset.UtcNow;

        var submit = CreateContext(new SubmitCheckout(12.5m), Command);
        await inbox.HandleAsync(submit.Context, TestContext.Current.CancellationToken);
        await inbox.HandleAsync(CreateContext(new SubmitCheckout(12.5m), Command).Context, TestContext.Current.CancellationToken);

        Assert.Equal(CheckoutStatus.Submitted, await grain.GetStatusAsync());
        Assert.Equal(new[] { "complete" }, submit.Events);
        var scheduled = Assert.Single(outputs);
        Assert.Equal(Receiver, scheduled.ReceiverId);
        Assert.Equal(new MessageId(Command.Key.CreateChildKey("grace-period")), scheduled.Envelope.MessageId);
        Assert.Equal("checkout.grace-period-elapsed.v1", scheduled.Envelope.Subject);
        Assert.InRange(scheduled.Envelope.DeliverAt!.Value, before + CheckoutGrain.GracePeriod, DateTimeOffset.UtcNow + CheckoutGrain.GracePeriod);

        var elapsed = CreateContext(new GracePeriodElapsed(), scheduled.Envelope.MessageId);
        await inbox.HandleAsync(elapsed.Context, TestContext.Current.CancellationToken);
        Assert.Equal(CheckoutStatus.Confirmed, await grain.GetStatusAsync());
        Assert.Equal(new[] { "complete" }, elapsed.Events);
    }

    [Fact]
    public async Task GracePeriod_CancelledCheckoutCompletesElapsedStepWithoutConfirming()
    {
        var status = new TestValue<CheckoutStatus> { Value = CheckoutStatus.Submitted };
        var state = Substitute.For<IDurableStateManager>();
        var inbox = new TestInbox();
        var grain = new CheckoutGrain(inbox, Substitute.For<IOutbox>(), state, _messages, status, Substitute.For<IGrainContext>());
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);

        await grain.CancelAsync();
        var elapsed = CreateContext(new GracePeriodElapsed(), Command);
        await inbox.HandleAsync(elapsed.Context, TestContext.Current.CancellationToken);

        Assert.Equal(CheckoutStatus.Cancelled, await grain.GetStatusAsync());
        Assert.Equal(new[] { "complete" }, elapsed.Events);
        await state.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Campaign_FanoutStagesFrozenRecipientIntentsBeforeAwaitingAcknowledgement()
    {
        var campaigns = new TestDictionary<Guid, NotificationCampaign>();
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainId.Returns(Sender);
        var grain = new CampaignGrain(outbox, state, campaigns, _messages, grainContext);
        var id = Guid.Parse("3dd3fbec-0197-47cf-9233-92ecbddcd057");
        GrainId[] original = [GrainId.Create("notification", "alice"), GrainId.Create("notification", "bob")];
        var recipients = original.ToArray();
        var outputs = new List<OutboxMessage>();
        var events = new List<string>();
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call =>
        {
            outputs.Add(call.Arg<OutboxMessage>());
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
        Assert.Equal(2, outputs.Select(message => message.Envelope.MessageId).Distinct().Count());
        var root = MessageId.Create("campaigns", id.ToString("N"));
        for (var index = 0; index < outputs.Count; index++)
        {
            var envelope = outputs[index];
            Assert.Equal(new MessageId(root.Key.CreateChildKey(original[index].ToString())), envelope.Envelope.MessageId);
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
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(Sender);
        var grain = new CampaignGrain(outbox, state, campaigns, _messages, context);

        await Assert.ThrowsAsync<ArgumentException>(() => grain.PublishAsync(id,
            changeRecipients ? "original" : "changed",
            changeRecipients ? [GrainId.Create("notification", "bob")] : original));

        Assert.Same(campaign, Assert.Single(campaigns).Value);
        Assert.Equal(original, campaign.Recipients);
        outbox.DidNotReceive().Send(Arg.Any<OutboxMessage>());
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Campaign_InvalidLaterCommandPreservesEarlierStagedIntent()
    {
        var campaigns = new TestDictionary<Guid, NotificationCampaign>();
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var context = Substitute.For<IGrainContext>();
        var grain = new CampaignGrain(outbox, state, campaigns, _messages, context);
        var output = new List<OutboxMessage>();
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call =>
            output.Add(call.Arg<OutboxMessage>()));
        var id = Guid.NewGuid();
        GrainId[] recipients =
        [
            GrainId.Create("notification", "alice"),
            GrainId.Create("notification", new string('x', 1024))
        ];

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.PublishAsync(id, "campaign text", recipients));

        Assert.Empty(campaigns);
        var earlier = Assert.Single(output);
        Assert.Equal(recipients[0], earlier.ReceiverId);
        Assert.Equal(new MessageId(HierarchicalKey.Create("campaigns", id.ToString("N")).CreateChildKey(recipients[0].ToString())),
            earlier.Envelope.MessageId);
        Assert.Equal(new Notify("campaign text"), ReadBody<Notify>(earlier));
        outbox.Received(1).Send(Arg.Any<OutboxMessage>());
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Campaign_RawSendFailureLeavesCampaignStateUnchanged()
    {
        var campaigns = new TestDictionary<Guid, NotificationCampaign>();
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var context = Substitute.For<IGrainContext>();
        var sentinel = new IOException("staging failed");
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(_ => throw sentinel);
        var grain = new CampaignGrain(outbox, state, campaigns, _messages, context);

        Assert.Same(sentinel, await Assert.ThrowsAsync<IOException>(() =>
            grain.PublishAsync(Guid.NewGuid(), "campaign text", [GrainId.Create("notification", "alice")])));

        Assert.Empty(campaigns);
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Campaign_LaterStagingFailureRetriesOriginalRecipientIdentities()
    {
        var campaigns = new TestDictionary<Guid, NotificationCampaign>();
        var outbox = Substitute.For<IOutbox>();
        var state = Substitute.For<IDurableStateManager>();
        var context = Substitute.For<IGrainContext>();
        var sentinel = new IOException("second staging failed");
        var grain = new CampaignGrain(outbox, state, campaigns, _messages, context);
        var output = new Dictionary<MessageId, OutboxMessage>();
        var sends = 0;
        outbox.When(value => value.Send(Arg.Any<OutboxMessage>())).Do(call =>
        {
            if (++sends == 2) throw sentinel;
            var envelope = call.Arg<OutboxMessage>();
            if (output.TryGetValue(envelope.Envelope.MessageId, out var existing))
            {
                Assert.Equal(existing.ReceiverId, envelope.ReceiverId);
                Assert.Equal(existing.Envelope.Payload.ToArray(), envelope.Envelope.Payload.ToArray());
            }
            else output.Add(envelope.Envelope.MessageId, envelope);
        });
        var id = Guid.NewGuid();
        GrainId[] recipients = [GrainId.Create("notification", "alice"), GrainId.Create("notification", "bob")];

        Assert.Same(sentinel, await Assert.ThrowsAsync<IOException>(() =>
            grain.PublishAsync(id, "campaign text", recipients)));
        Assert.Equal(recipients[0], Assert.Single(output).Value.ReceiverId);
        Assert.Empty(campaigns);
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());

        await grain.PublishAsync(id, "campaign text", recipients);

        Assert.Equal(2, output.Count);
        var root = MessageId.Create("campaigns", id.ToString("N"));
        foreach (var recipient in recipients)
        {
            var message = output[new MessageId(root.Key.CreateChildKey(recipient.ToString()))];
            Assert.Equal(recipient, message.ReceiverId);
            Assert.Equal(new Notify("campaign text"), ReadBody<Notify>(message));
        }
        Assert.Equal(4, sends);
        Assert.Equal(recipients, Assert.Single(campaigns).Value.Recipients);
        await state.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }
    private (IInboxHandlerContext Context, List<OutboxMessage> Output, List<string> Events) CreateContext<T>(T body, MessageId key)
    {
        var message = Type<T>().Create(key, Receiver, body).ToInboxMessage();
        var context = Substitute.For<IInboxHandlerContext>();
        var output = new List<OutboxMessage>();
        var events = new List<string>();
        context.Message.Returns(_ =>
        {
            _activeAttempt = (output, events);
            return message;
        });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        context.When(value => value.Fail(Arg.Any<string>())).Do(_ => events.Add("dead-letter"));
        return (context, output, events);
    }

    private T ReadBody<T>(OutboxMessage message) => Type<T>().Decode(message.Envelope);

    private T ReadBody<T>(InboxMessage message) => Type<T>().Decode(message.Envelope);

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
