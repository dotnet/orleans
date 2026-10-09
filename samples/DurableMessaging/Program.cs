using DurableMessaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<CommittedReceiptsProbe>();
builder.UseOrleans(silo => silo
    .UseLocalhostClustering()
    .UseInMemoryDurableJobs()
    .AddVolatileJournalStorage()
    .AddDurableMessaging());

using var host = builder.Build();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await host.StartAsync(timeout.Token);
try
{
    var client = host.Services.GetRequiredService<IClusterClient>();
    var stock = client.GetGrain<IStockGrain>("trail-shoes");
    var order = client.GetGrain<IOrderGrain>("order-1042");
    var operation = HierarchicalKey.Create("orders", "order-1042", "reserve-stock", "v1");
    await stock.InitializeAsync(10).WaitAsync(timeout.Token);

    var first = await order.ReserveAsync(stock.GetGrainId(), operation, 2).WaitAsync(timeout.Token);
    var second = await order.ReserveAsync(stock.GetGrainId(), operation, 2).WaitAsync(timeout.Token);
    Require(first != second, "Each submission must have a fresh transport message ID.");
    Console.WriteLine($"Submitted {operation} twice: {first} and {second}");

    var receipts = await host.Services.GetRequiredService<CommittedReceiptsProbe>()
        .Completion.WaitAsync(timeout.Token);
    var snapshot = await stock.GetSnapshotAsync().WaitAsync(timeout.Token);
    Require(receipts.Length == 2 && receipts[0] == receipts[1],
        "Both acknowledged replies must contain the same original business outcome.");
    var outcome = receipts[0];
    Require(outcome.Operation.Equals(operation) && outcome.Accepted && outcome.Quantity == 2
        && outcome.ReservationId != Guid.Empty && outcome.RemainingStock == 8,
        "The original outcome must reserve two units from ten.");
    Require(snapshot.Inventory is { Remaining: 8, Reservations: 1, ProcessedRequests: 2 }
        && snapshot.LedgerEntries == 1,
        "Two distinct deliveries must produce one business effect and one ledger entry.");

    Console.WriteLine($"ACKNOWLEDGED: two replies, original reservation {outcome.ReservationId}");
    Console.WriteLine("VERIFIED: remaining stock=8, reservations=1, processed requests=2, ledger entries=1.");
    Console.WriteLine("Volatile storage is for this demonstration only; stopping the host discards all journals and jobs.");
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
