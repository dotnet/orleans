using System.Collections.Concurrent;
using BroadcastChannel.GrainInterfaces;
using Orleans.BroadcastChannel;

namespace BroadcastChannel.Silo;

[ImplicitChannelSubscription]
public sealed class LiveStockGrain :
    Grain,
    ILiveStockGrain,
    IOnBroadcastChannelSubscribed
{
    private readonly IDictionary<StockSymbol, Stock> _stockCache =
        new ConcurrentDictionary<StockSymbol, Stock>();

    public ValueTask<Stock> GetStock(StockSymbol symbol) =>
        _stockCache.TryGetValue(symbol, out Stock? stock) is false
            ? new ValueTask<Stock>(Task.FromException<Stock>(new KeyNotFoundException()))
            : new ValueTask<Stock>(stock);

    public Task OnSubscribed(IBroadcastChannelSubscription subscription) =>
        subscription.Attach<Stock>(OnStockUpdated, OnError);

    private Task OnStockUpdated(Stock stock)
    {
        if (stock is { GlobalQuote: { } })
        {
            _stockCache[stock.GlobalQuote.Symbol] = stock;
        }

        return Task.CompletedTask;
    }

    private static Task OnError(Exception ex)
    {
        Console.Error.WriteLine($"An error occurred: {ex}");

        return Task.CompletedTask;
    }
}
