namespace WorkflowsApp.Service;

[Alias("IDictionaryGrain`2")]
public interface IDictionaryGrain<K, V> : IGrainWithStringKey where K : notnull
{
    [Alias("TryGetValueAsync")]
    ValueTask<(bool Success, V? Value, long Version)> TryGetValueAsync(K key);
    [Alias("TryAddAsync")]
    ValueTask<(bool Success, long Version)> TryAddAsync(K key, V value, long version);
    [Alias("SetAsync")]
    ValueTask<(bool Success, long Version)> SetAsync(K key, V value, long version);
    [Alias("RemoveAsync")]
    ValueTask<(bool Success, long Version)> RemoveAsync(K key, long version);
    [Alias("ClearAsync")]
    ValueTask<(bool Success, long Version)> ClearAsync(long version);
    [Alias("GetValuesAsync")]
    IAsyncEnumerable<(K Key, V Value, long Version)> GetValuesAsync();
}
