namespace BroadcastChannel.GrainInterfaces;

public interface ILiveStockGrain : IGrainWithGuidKey
{
    ValueTask<Stock> GetStock(StockSymbol symbol);
}