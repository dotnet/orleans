namespace WorkflowsApp.Service;

public interface IShoppingCartGrain
{
    ValueTask<(bool Success, long Version)> UpdateItem(string itemId, int quantity, long version);
    ValueTask<(Dictionary<string, int> Contents, long Version)> GetCart();
    ValueTask<long> GetVersion();
    ValueTask<(bool Success, long Version)> Clear(long version);
}
