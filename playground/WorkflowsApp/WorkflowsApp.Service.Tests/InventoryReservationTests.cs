using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Journaling.Messaging;
using Orleans.Runtime.DurableTasks;
using Orleans.TestingHost;
using WorkflowsApp.Service.Samples.InventoryReservation;
using Xunit;

namespace WorkflowsApp.Service.Tests;

[TestCategory("BVT")]
public sealed class InventoryReservationTests : IClassFixture<InventoryReservationTests.Fixture>
{
    private readonly Fixture _fixture;

    public InventoryReservationTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AggregateOrderItems_CombinesDuplicateSkus()
    {
        var result = InventoryReservation.AggregateOrderItems(
        [
            new InventoryReservation.OrderItem { Sku = "Widget", Quantity = 3 },
            new InventoryReservation.OrderItem { Sku = "Widget", Quantity = 4 }
        ]);

        Assert.Equal(7, result["Widget"]);
        Assert.Single(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AggregateOrderItems_RejectsNonPositiveQuantities(int quantity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            InventoryReservation.AggregateOrderItems(
            [
                new InventoryReservation.OrderItem { Sku = "Widget", Quantity = quantity }
            ]));

        Assert.Contains("must be positive", exception.Message);
    }

    [Fact]
    public async Task ReserveItems_RejectsAggregatedOverdrawWithoutMutatingStock()
    {
        var (inventory, notifications) = CreateGrains();
        _ = await notifications.GetNotificationsAsync();
        await inventory.AddStockAsync("Widget", 7);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inventory.ReserveItemsAsync(
                "overdraw",
                [
                    new InventoryReservation.OrderItem { Sku = "Widget", Quantity = 4 },
                    new InventoryReservation.OrderItem { Sku = "Widget", Quantity = 4 }
                ],
                notifications));

        Assert.Contains("requested 8, available 7", exception.Message);
        Assert.Equal(7, (await inventory.GetAllStockAsync())["Widget"]);
        var notification = Assert.Single(await WaitForNotificationsAsync(notifications, 1));
        Assert.StartsWith("[FAILED]", notification);
    }

    [Fact]
    public async Task ReserveItems_IsIdempotentByOrderIdAndDeliversOneDurableNotification()
    {
        var (inventory, notifications) = CreateGrains();
        _ = await notifications.GetNotificationsAsync();
        await inventory.AddStockAsync("Widget", 10);
        var items = new List<InventoryReservation.OrderItem>
        {
            new() { Sku = "Widget", Quantity = 2 },
            new() { Sku = "Widget", Quantity = 3 }
        };

        var firstReservation = await inventory.ReserveItemsAsync("order-1", items, notifications);
        var secondReservation = await inventory.ReserveItemsAsync("order-1", items, notifications);

        Assert.Equal(firstReservation, secondReservation);
        Assert.Equal(5, (await inventory.GetAllStockAsync())["Widget"]);

        var notification = Assert.Single(await WaitForNotificationsAsync(notifications, 1));
        Assert.Contains("order-1", notification);
        Assert.Contains("Widget=5", notification);

        await Task.Delay(200);
        Assert.Single(await notifications.GetNotificationsAsync());
    }

    private (InventoryReservation.IInventoryGrain Inventory, InventoryReservation.INotificationServiceGrain Notifications) CreateGrains()
    {
        var key = Guid.NewGuid().ToString("N");
        return (
            _fixture.Cluster.Client.GetGrain<InventoryReservation.IInventoryGrain>($"inventory-{key}"),
            _fixture.Cluster.Client.GetGrain<InventoryReservation.INotificationServiceGrain>($"notifications-{key}"));
    }

    private static async Task<List<string>> WaitForNotificationsAsync(
        InventoryReservation.INotificationServiceGrain grain,
        int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var result = await grain.GetNotificationsAsync();
            if (result.Count >= count)
            {
                return result;
            }

            await Task.Delay(20, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        throw new TimeoutException($"Timed out waiting for {count} notifications.");
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public Fixture()
        {
            var builder = new InProcessTestClusterBuilder();
            var storageProvider = new VolatileStateMachineStorageProvider();
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.AddDurableTasks();
                siloBuilder.AddStateMachineStorage();
                siloBuilder.AddDurableMessaging();
                siloBuilder.AddJournaledDurableTaskStorage();
                siloBuilder.Services.AddSingleton<IStateMachineStorageProvider>(storageProvider);
            });
            Cluster = builder.Build();
        }

        public InProcessTestCluster Cluster { get; }

        public Task InitializeAsync() => Cluster.DeployAsync();

        public Task DisposeAsync() => Cluster.DisposeAsync().AsTask();
    }
}
