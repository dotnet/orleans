using eShop.Domain.Stock;

namespace GrainsAbstraction;

public interface IStockGrain : IGrainWithGuidKey
{
    public Task MoveStockItem(GrainCancellationToken grainCancellationToken, Guid itemId, int quantity);
}