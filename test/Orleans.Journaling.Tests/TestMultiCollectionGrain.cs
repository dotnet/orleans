using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Tests;

public class TestMultiCollectionGrain(
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("list")] IDurableList<string> list,
    [FromKeyedServices("queue")] IDurableQueue<int> queue,
    [FromKeyedServices("set")] IDurableSet<string> set) : DurableGrain, ITestMultiCollectionGrain
{
    private readonly Guid _activationId = Guid.NewGuid();

    // Dictionary operations
    public async Task AddToDictionary(string key, int value)
    {
        dictionary[key] = value;
        await WriteStateAsync();
    }

    public async Task RemoveFromDictionary(string key)
    {
        dictionary.Remove(key);
        await WriteStateAsync();
    }

    public async Task<int> GetDictionaryValue(string key)
    {
        return await Task.FromResult(dictionary[key]);
    }

    public async Task<int> GetDictionaryCount()
    {
        return await Task.FromResult(dictionary.Count);
    }

    // List operations
    public async Task AddToList(string item)
    {
        list.Add(item);
        await WriteStateAsync();
    }

    public async Task RemoveListItemAt(int index)
    {
        list.RemoveAt(index);
        await WriteStateAsync();
    }

    public async Task<string> GetListItem(int index)
    {
        return await Task.FromResult(list[index]);
    }

    public async Task<int> GetListCount()
    {
        return await Task.FromResult(list.Count);
    }

    // Queue operations
    public async Task AddToQueue(int item)
    {
        queue.Enqueue(item);
        await WriteStateAsync();
    }

    public async Task<int> DequeueItem()
    {
        var item = queue.Dequeue();
        return await Task.FromResult(item);
    }

    public async Task<int> PeekQueueItem()
    {
        return await Task.FromResult(queue.Peek());
    }

    public async Task<int> GetQueueCount()
    {
        return await Task.FromResult(queue.Count);
    }

    // Set operations
    public async Task AddToSet(string item)
    {
        set.Add(item);
        await WriteStateAsync();
    }

    public async Task RemoveFromSet(string item)
    {
        set.Remove(item);
        await WriteStateAsync();
    }

    public async Task<bool> ContainsSetItem(string item)
    {
        return await Task.FromResult(set.Contains(item));
    }

    public async Task<int> GetSetCount()
    {
        return await Task.FromResult(set.Count);
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
}
