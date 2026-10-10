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
builder.Services.AddDurableMessageType<ReserveStock>(StockProtocol.Reserve);
builder.Services.AddDurableMessageType<Restock>(StockProtocol.Restock);
builder.Services.AddDurableMessageType<ReservationOutcome>(StockProtocol.Result);
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
    var commandId = HierarchicalKey.Create("orders", "order-1042", "reserve-stock");
    await stock.InitializeAsync(10).WaitAsync(timeout.Token);

    var first = await order.ReserveAsync(stock.GetGrainId(), commandId, 2).WaitAsync(timeout.Token);
    Require(first == commandId, "The envelope must preserve the application's command ID.");

    var receipts = await host.Services.GetRequiredService<CommittedReceiptsProbe>()
        .Completion.WaitAsync(timeout.Token);
    var duplicate = await order.ResubmitAsync(stock.GetGrainId(), commandId, 2).WaitAsync(timeout.Token);
    Require(duplicate.Status == DeliveryStatus.Duplicate, "Resubmission must recognize the completed command.");
    var snapshot = await stock.GetSnapshotAsync().WaitAsync(timeout.Token);
    Require(receipts.Length == 1, "The original command must produce one acknowledged reply.");
    var outcome = receipts[0];
    Require(outcome.CommandId == commandId && outcome.Accepted && outcome.Quantity == 2
        && outcome.RemainingStock == 8,
        "The original outcome must reserve two units from ten.");
    Require(snapshot.Inventory is { Remaining: 8, Reservations: 1, ProcessedRequests: 1 },
        "Two submissions of one command must produce one handler execution and one business effect.");

    Console.WriteLine($"ACKNOWLEDGED: original reply {commandId.CreateChildKey("result")}");
    Console.WriteLine($"RESUBMITTED: {commandId}, admission={duplicate.Status}");
    Console.WriteLine("VERIFIED: remaining stock=8, reservations=1, processed requests=1.");
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
