using Orleans.Journaling;
using Orleans.Journaling.Messaging;
using Orleans.Serialization.Session;

namespace WorkflowsApp.Service.Samples.InventoryReservation;

/// <summary>
/// Demonstrates atomic inventory updates and notifications using durable dictionaries and an outbox/inbox pair.
/// </summary>
internal static class InventoryReservation
{
    private const string ReservedRoute = "inventory/reserved";
    private const string FailedRoute = "inventory/reservation-failed";

    public static async Task RunAsync(IServiceProvider services)
    {
        var grainFactory = services.GetRequiredService<IGrainFactory>();
        var sampleId = Guid.NewGuid().ToString("N");
        var inventory = grainFactory.GetGrain<IInventoryGrain>($"warehouse-{sampleId}");
        var notifications = grainFactory.GetGrain<INotificationServiceGrain>($"notifications-{sampleId}");

        Console.WriteLine("--- InventoryReservation Sample: durable outbox/inbox notifications ---");

        _ = await notifications.GetNotificationsAsync();
        await inventory.AddStockAsync("Widget-A", 20);
        await inventory.AddStockAsync("Widget-B", 10);

        var orderId = $"order-{sampleId}";
        var items = new List<OrderItem>
        {
            new() { Sku = "Widget-A", Quantity = 3 },
            new() { Sku = "Widget-A", Quantity = 2 },
            new() { Sku = "Widget-B", Quantity = 4 }
        };

        var reservationId = await inventory.ReserveItemsAsync(orderId, items, notifications);
        var duplicateReservationId = await inventory.ReserveItemsAsync(orderId, items, notifications);
        if (reservationId != duplicateReservationId)
        {
            throw new InvalidOperationException("Retrying an order must return the original reservation.");
        }

        await WaitForNotificationCountAsync(notifications, 1);

        var stock = await inventory.GetAllStockAsync();
        if (stock["Widget-A"] != 15 || stock["Widget-B"] != 6)
        {
            throw new InvalidOperationException("Duplicate SKUs were not aggregated or the order was applied more than once.");
        }

        try
        {
            await inventory.ReserveItemsAsync(
                $"invalid-{sampleId}",
                [new OrderItem { Sku = "Widget-A", Quantity = 0 }],
                notifications);
            throw new InvalidOperationException("A zero quantity was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        try
        {
            await inventory.ReserveItemsAsync(
                $"unavailable-{sampleId}",
                [new OrderItem { Sku = "Widget-B", Quantity = 100 }],
                notifications);
            throw new InvalidOperationException("An order with insufficient stock was accepted.");
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Insufficient stock", StringComparison.Ordinal))
        {
        }

        await WaitForNotificationCountAsync(notifications, 2);
        foreach (var notification in await notifications.GetNotificationsAsync())
        {
            Console.WriteLine($"  {notification}");
        }

        Console.WriteLine($"Reservation {reservationId} completed and duplicate delivery was suppressed.");
    }

    internal static Dictionary<string, int> AggregateOrderItems(IEnumerable<OrderItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null)
            {
                throw new ArgumentException("Order items cannot contain null values.", nameof(items));
            }

            var sku = ValidateSku(item.Sku, nameof(items));
            if (item.Quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(items), item.Quantity, $"Quantity for '{sku}' must be positive.");
            }

            result[sku] = checked(result.GetValueOrDefault(sku) + item.Quantity);
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("At least one order item is required.", nameof(items));
        }

        return result;
    }

    private static string ValidateSku(string sku, string paramName)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new ArgumentException("An SKU is required.", paramName);
        }

        return sku.Trim();
    }

    private static async Task WaitForNotificationCountAsync(INotificationServiceGrain grain, int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if ((await grain.GetNotificationsAsync()).Count >= expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} durable notifications.");
    }

    [GenerateSerializer]
    public sealed class InventoryReservedEvent
    {
        [Id(0)] public required string ReservationId { get; init; }
        [Id(1)] public required string OrderId { get; init; }
        [Id(2)] public required Dictionary<string, int> ReservedItems { get; init; }
        [Id(3)] public DateTimeOffset Timestamp { get; init; }
    }

    [GenerateSerializer]
    public sealed class ReservationFailedEvent
    {
        [Id(0)] public required string OrderId { get; init; }
        [Id(1)] public required string Reason { get; init; }
        [Id(2)] public DateTimeOffset Timestamp { get; init; }
    }

    [GenerateSerializer]
    public sealed class OrderItem
    {
        [Id(0)] public required string Sku { get; init; }
        [Id(1)] public int Quantity { get; init; }
    }

    [Alias("WorkflowsApp.Service.Samples.InventoryReservation.IInventoryGrain")]
    public interface IInventoryGrain : IGrainWithStringKey
    {
        [Alias("AddStock")]
        Task AddStockAsync(string sku, int quantity);

        [Alias("GetAllStock")]
        Task<Dictionary<string, int>> GetAllStockAsync();

        [Alias("ReserveItems")]
        Task<string> ReserveItemsAsync(string orderId, List<OrderItem> items, INotificationServiceGrain notificationService);
    }

    [Alias("WorkflowsApp.Service.Samples.InventoryReservation.INotificationServiceGrain")]
    public interface INotificationServiceGrain : IGrainWithStringKey
    {
        [Alias("GetNotifications")]
        Task<List<string>> GetNotificationsAsync();
    }

    internal sealed class InventoryGrain(
        [FromKeyedServices("inventory-stock")] IDurableDictionary<string, int> stock,
        [FromKeyedServices("inventory-reservations")] IDurableDictionary<string, ReservationRecord> reservations,
        IDurableOutbox outbox,
        SerializerSessionPool sessionPool)
        : DurableGrain, IInventoryGrain
    {
        private readonly IDurableDictionary<string, int> _stock = stock;
        private readonly IDurableDictionary<string, ReservationRecord> _reservations = reservations;
        private readonly IDurableOutbox _outbox = outbox;
        private readonly SerializerSessionPool _sessionPool = sessionPool;

        public async Task AddStockAsync(string sku, int quantity)
        {
            sku = ValidateSku(sku, nameof(sku));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

            var current = _stock.TryGetValue(sku, out var existing) ? existing : 0;
            _stock[sku] = checked(current + quantity);
            await WriteStateAsync();
        }

        public Task<Dictionary<string, int>> GetAllStockAsync()
            => Task.FromResult(new Dictionary<string, int>(_stock, StringComparer.Ordinal));

        public async Task<string> ReserveItemsAsync(
            string orderId,
            List<OrderItem> items,
            INotificationServiceGrain notificationService)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
            ArgumentNullException.ThrowIfNull(notificationService);

            orderId = orderId.Trim();
            if (_reservations.TryGetValue(orderId, out var existing))
            {
                if (existing.FailureReason is { } failureReason)
                {
                    throw new InvalidOperationException(failureReason);
                }

                return existing.ReservationId;
            }

            var requested = AggregateOrderItems(items);
            foreach (var (sku, quantity) in requested)
            {
                var available = _stock.TryGetValue(sku, out var current) ? current : 0;
                if (available < quantity)
                {
                    var reason = $"Insufficient stock for {sku}: requested {quantity}, available {available}";
                    _reservations[orderId] = ReservationRecord.Failed(orderId, requested, reason);
                    Send(notificationService, FailedRoute, new ReservationFailedEvent
                    {
                        OrderId = orderId,
                        Reason = reason,
                        Timestamp = DateTimeOffset.UtcNow
                    });
                    await WriteStateAsync();
                    throw new InvalidOperationException(reason);
                }
            }

            var reservationId = $"RES-{Guid.NewGuid():N}";
            foreach (var (sku, quantity) in requested)
            {
                _stock[sku] -= quantity;
            }

            _reservations[orderId] = ReservationRecord.Succeeded(reservationId, orderId, requested);
            Send(notificationService, ReservedRoute, new InventoryReservedEvent
            {
                ReservationId = reservationId,
                OrderId = orderId,
                ReservedItems = requested,
                Timestamp = DateTimeOffset.UtcNow
            });

            await WriteStateAsync();
            return reservationId;
        }

        private void Send<T>(INotificationServiceGrain receiver, string route, T body)
        {
            var envelope = new DurableEnvelopeBuilder(_sessionPool, this.GetGrainId())
                .To(receiver.GetGrainId(), route)
                .WithBody(body)
                .WithCorrelationKey(this.GetPrimaryKeyString())
                .Build();
            _outbox.Send(envelope);
        }
    }

    [GenerateSerializer]
    public sealed class ReservationRecord
    {
        [Id(0)] public required string ReservationId { get; init; }
        [Id(1)] public required string OrderId { get; init; }
        [Id(2)] public required Dictionary<string, int> Items { get; init; }
        [Id(3)] public DateTimeOffset CreatedAt { get; init; }
        [Id(4)] public string? FailureReason { get; init; }

        public static ReservationRecord Succeeded(string reservationId, string orderId, Dictionary<string, int> items)
            => new()
            {
                ReservationId = reservationId,
                OrderId = orderId,
                Items = items,
                CreatedAt = DateTimeOffset.UtcNow
            };

        public static ReservationRecord Failed(string orderId, Dictionary<string, int> items, string reason)
            => new()
            {
                ReservationId = string.Empty,
                OrderId = orderId,
                Items = items,
                CreatedAt = DateTimeOffset.UtcNow,
                FailureReason = reason
            };
    }

    internal sealed class NotificationServiceGrain(
        [FromKeyedServices("notifications-log")] IDurableDictionary<string, NotificationRecord> notifications,
        IDurableInbox inbox)
        : DurableGrain, INotificationServiceGrain
    {
        private readonly IDurableDictionary<string, NotificationRecord> _notifications = notifications;
        private readonly IDurableInbox _inbox = inbox;

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _inbox.RegisterHandler(new InventoryReservedHandler(this));
            _inbox.RegisterHandler(new ReservationFailedHandler(this));
            return base.OnActivateAsync(cancellationToken);
        }

        public Task<List<string>> GetNotificationsAsync()
            => Task.FromResult(_notifications.Values.OrderBy(static value => value.Timestamp).Select(static value => value.Message).ToList());

        private void Record(string orderId, string message, DateTimeOffset timestamp)
        {
            if (!_notifications.ContainsKey(orderId))
            {
                _notifications[orderId] = new NotificationRecord
                {
                    OrderId = orderId,
                    Message = message,
                    Timestamp = timestamp
                };
            }
        }

        private sealed class InventoryReservedHandler(NotificationServiceGrain grain) : IInboxHandler<InventoryReservedEvent>
        {
            public bool CanHandle(IInboxHandlerContext context) => context.Envelope.RouteKey == ReservedRoute;

            public ValueTask HandleAsync(
                InventoryReservedEvent message,
                IInboxHandlerContext context,
                CancellationToken cancellationToken)
            {
                var items = string.Join(", ", message.ReservedItems.Select(static item => $"{item.Key}={item.Value}"));
                grain.Record(message.OrderId, $"[SUCCESS] Reservation {message.ReservationId} for order {message.OrderId}: {items}", message.Timestamp);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class ReservationFailedHandler(NotificationServiceGrain grain) : IInboxHandler<ReservationFailedEvent>
        {
            public bool CanHandle(IInboxHandlerContext context) => context.Envelope.RouteKey == FailedRoute;

            public ValueTask HandleAsync(
                ReservationFailedEvent message,
                IInboxHandlerContext context,
                CancellationToken cancellationToken)
            {
                grain.Record(message.OrderId, $"[FAILED] Order {message.OrderId}: {message.Reason}", message.Timestamp);
                return ValueTask.CompletedTask;
            }
        }
    }

    [GenerateSerializer]
    public sealed class NotificationRecord
    {
        [Id(0)] public required string OrderId { get; init; }
        [Id(1)] public required string Message { get; init; }
        [Id(2)] public DateTimeOffset Timestamp { get; init; }
    }
}
