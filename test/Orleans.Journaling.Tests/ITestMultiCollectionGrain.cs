namespace Orleans.Journaling.Tests;

/// <summary>
/// Interface for the test multi-collection grain
/// </summary>
public interface ITestMultiCollectionGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();

    // Dictionary operations
    Task AddToDictionary(string key, int value);
    Task RemoveFromDictionary(string key);
    Task<int> GetDictionaryValue(string key);
    Task<int> GetDictionaryCount();

    // List operations
    Task AddToList(string item);
    Task RemoveListItemAt(int index);
    Task<string> GetListItem(int index);
    Task<int> GetListCount();

    // Queue operations
    Task AddToQueue(int item);
    Task<int> DequeueItem();
    Task<int> PeekQueueItem();
    Task<int> GetQueueCount();

    // Set operations
    Task AddToSet(string item);
    Task RemoveFromSet(string item);
    Task<bool> ContainsSetItem(string item);
    Task<int> GetSetCount();
}