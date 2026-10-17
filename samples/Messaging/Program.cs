using Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Messaging;
using Orleans.Hosting;
using Orleans.Journaling;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<CommittedReceiptsProbe>();
builder.Services.AddSingleton<StockMessages>();

builder.UseOrleans(silo => silo
    .UseLocalhostClustering()
    .UseInMemoryDurableJobs()
    .AddVolatileJournalStorage()
    .ConfigureServices(services => services.Configure<JournaledStateManagerOptions>(
        options => options.JournalFormatKey = "orleans-binary"))
    .AddMessaging());

using var host = builder.Build();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await host.StartAsync(timeout.Token);
try
{
    var client = host.Services.GetRequiredService<IClusterClient>();
    var stock = client.GetGrain<IStockGrain>("trail-shoes");
    var order = client.GetGrain<IOrderGrain>("order-1042");
    var commandId = MessageId.Create("orders", "order-1042", "reserve-stock");
    var probe = host.Services.GetRequiredService<CommittedReceiptsProbe>();
    await stock.InitializeAsync(10).WaitAsync(timeout.Token);

    (MessageId Id, int Quantity, ReservationRejectionReason? Rejection)[] commands =
    [
        (commandId, 2, null),
        (MessageId.Create("orders", "order-1042", "reserve-too-much"), 9,
            ReservationRejectionReason.InsufficientStock),
        (MessageId.Create("orders", "order-1042", "reserve-invalid-quantity"), 0,
            ReservationRejectionReason.InvalidQuantity)
    ];
    for (var index = 0; index < commands.Length; index++)
    {
        var command = commands[index];
        var acknowledgement = probe.WaitForAsync(command.Id);
        var submitted = await order.ReserveAsync(stock.GetGrainId(), command.Id, command.Quantity)
            .WaitAsync(timeout.Token);
        Require(submitted == command.Id, "The envelope must preserve the application's command ID.");

        var receipts = await acknowledgement.WaitAsync(timeout.Token);
        Require(receipts.Length == index + 1 && receipts.Select(receipt => receipt.CommandId).Distinct().Count() == index + 1,
            "Every independent command must produce exactly one acknowledged reply.");
        var outcome = receipts.Single(receipt => receipt.CommandId == command.Id);
        Require(outcome.Quantity == command.Quantity && outcome.RemainingStock == 8,
            "Only the accepted reservation may consume stock.");
        var expectedOutcome = (command.Rejection, outcome) switch
        {
            (null, ReservationAccepted) => true,
            ({ } reason, ReservationRejected rejected) => rejected.Reason == reason,
            _ => false
        };
        Require(expectedOutcome,
            "The reply must distinguish acceptance, insufficient stock, and invalid quantity.");

        var duplicate = await order.ResubmitAsync(stock.GetGrainId(), command.Id, command.Quantity).WaitAsync(timeout.Token);
        Require(duplicate.Status == DeliveryStatus.Duplicate, "Resubmission must recognize the completed command.");
        var snapshot = await stock.GetSnapshotAsync().WaitAsync(timeout.Token);
        Require(snapshot.Inventory is { Remaining: 8, Reservations: 1 }
                && snapshot.Inventory.ProcessedRequests == index + 1,
            "Resubmission must preserve one handler execution per command and unchanged stock for rejections.");

        var decision = nameof(ReservationAccepted);
        if (outcome is ReservationRejected rejection)
        {
            decision = $"ReservationRejected ({rejection.Reason})";
        }
        Console.WriteLine($"ACKNOWLEDGED: {new MessageId(command.Id.Key.CreateChildKey("result"))}, outcome={decision}");
        Console.WriteLine($"RESUBMITTED: {command.Id}, admission={duplicate.Status}");
    }
    Console.WriteLine("VERIFIED: remaining stock=8, reservations=1, processed requests=3.");
    Console.WriteLine("Volatile storage keeps journals and jobs in memory for the host's lifetime.");
}
finally
{
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await host.StopAsync(shutdown.Token);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
