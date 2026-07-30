using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using BroadcastChannel.GrainInterfaces;

using IHost host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient(client =>
    {
        client.UseLocalhostClustering();
        client.AddBroadcastChannel(
            ChannelNames.LiveStockTicker,
            builder => builder.Configure(
                options => options.FireAndForgetDelivery = false));
    })
    .UseConsoleLifetime()
    .Build();

await host.StartAsync();
await Task.Delay(3_000);

IGrainFactory factory = host.Services.GetRequiredService<IGrainFactory>();
ILiveStockGrain liveStocks = factory.GetGrain<ILiveStockGrain>(primaryKey: Guid.Empty);

foreach (StockSymbol symbol in Enum.GetValues<StockSymbol>())
{
    Stock stock = await liveStocks.GetStock(symbol);
    Console.WriteLine($"{symbol}: {stock.GlobalQuote.Price:C}");
}

await host.WaitForShutdownAsync();
