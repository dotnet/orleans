using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.DurableMessaging;
using Orleans.Runtime.DurableTasks;
using Orleans.TestingHost;
using WorkflowsApp.Service.Samples.HelloWorld;
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
            new OrderItem { Sku = "Widget", Quantity = 3 },
            new OrderItem { Sku = "Widget", Quantity = 4 }
        ]);

        Assert.Equal(7, result["Widget"]);
        Assert.Single(result);
    }

    [Fact]
    public void ShoppingCartContract_UsesStringGrainKey()
    {
        Assert.True(typeof(IGrainWithStringKey).IsAssignableFrom(typeof(IShoppingCartGrain)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AggregateOrderItems_RejectsNonPositiveQuantities(int quantity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            InventoryReservation.AggregateOrderItems(
            [
                new OrderItem { Sku = "Widget", Quantity = quantity }
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
                    new OrderItem { Sku = "Widget", Quantity = 4 },
                    new OrderItem { Sku = "Widget", Quantity = 4 }
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
        var items = new List<OrderItem>
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

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(await notifications.GetNotificationsAsync());
    }

    [Fact]
    public async Task DurableTask_RemoteCallsUseDurableMessaging()
    {
        var grain = _fixture.Cluster.Client.GetGrain<IHelloWorkflowGrain>(Guid.NewGuid().ToString("N"));
        var handle = await grain.RunSample().ScheduleAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var result = await handle.WaitAsync(timeout.Token);

        Assert.Equal(["Hello, Melbourne!", "Hello, Seattle!", "Hello, Shanghai!"], result);
    }

    private (IInventoryGrain Inventory, INotificationServiceGrain Notifications) CreateGrains()
    {
        var key = Guid.NewGuid().ToString("N");
        return (
            _fixture.Cluster.Client.GetGrain<IInventoryGrain>($"inventory-{key}"),
            _fixture.Cluster.Client.GetGrain<INotificationServiceGrain>($"notifications-{key}"));
    }

    private static async Task<List<string>> WaitForNotificationsAsync(
        INotificationServiceGrain grain,
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
            builder.ConfigureClient(clientBuilder => clientBuilder.AddDurableTasks());
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.AddDurableTasks();
                siloBuilder.UseInMemoryDurableJobs();
                siloBuilder.AddJournaledDurableTaskStorage();
                siloBuilder.Services.AddSingleton<VolatileJournalStorageProvider>();
                siloBuilder.Services.AddSingleton<IJournalStorageProvider>(
                    static serviceProvider => serviceProvider.GetRequiredService<VolatileJournalStorageProvider>());
            });
            Cluster = builder.Build();
        }

        public InProcessTestCluster Cluster { get; }

        public ValueTask InitializeAsync() => new(Cluster.DeployAsync());

        public ValueTask DisposeAsync() => Cluster.DisposeAsync();
    }
}
