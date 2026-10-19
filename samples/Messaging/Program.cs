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
    await stock.InitializeAsync(10).WaitAsync(timeout.Token);

    (MessageId Id, int Quantity, ReservationRejectionReason? Rejection)[] commands =
    [
        (commandId, 2, null),
        (MessageId.Create("orders", "order-1042", "reserve-too-much"), 9,
            ReservationRejectionReason.InsufficientStock),
        (MessageId.Create("orders", "order-1042", "reserve-invalid-quantity"), 0,
            ReservationRejectionReason.InvalidQuantity)
    ];
    foreach (var command in commands)
    {
        await order.ReserveAsync(stock.GetGrainId(), command.Id, command.Quantity).WaitAsync(timeout.Token);
        var outcome = await order.WaitForOutcomeAsync(command.Id).WaitAsync(timeout.Token);
        Require(outcome.CommandId == command.Id && outcome.Quantity == command.Quantity && outcome.RemainingStock == 8,
            "Each command receives its own reply, and only the accepted reservation consumes stock.");
        var expectedOutcome = (command.Rejection, outcome) switch
        {
            (null, ReservationAccepted) => true,
            ({ } reason, ReservationRejected rejected) => rejected.Reason == reason,
            _ => false
        };
        Require(expectedOutcome, "The reply must distinguish acceptance, insufficient stock, and invalid quantity.");

        var decision = outcome is ReservationRejected rejection ? $"ReservationRejected ({rejection.Reason})" : nameof(ReservationAccepted);
        Console.WriteLine($"ACKNOWLEDGED: {new MessageId(command.Id.Key.CreateChildKey("result"))}, outcome={decision}");
    }

    var snapshot = await stock.GetSnapshotAsync().WaitAsync(timeout.Token);
    Require(snapshot.Inventory is { Remaining: 8, Reservations: 1, ProcessedRequests: 3 },
        "The stock grain processes each command once.");    Console.WriteLine("VERIFIED: remaining stock=8, reservations=1, processed requests=3.");
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
