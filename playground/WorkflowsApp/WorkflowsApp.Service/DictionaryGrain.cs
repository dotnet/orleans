using Orleans.Journaling;

namespace WorkflowsApp.Service;

public class DictionaryGrain<K, V>(
    [FromKeyedServices("values")] IDurableDictionary<K, V> dictionary,
    [FromKeyedServices("version")] IDurableValue<long> version) : DurableGrain, IDictionaryGrain<K, V> where K : notnull
{
    private readonly IDurableValue<long> _version = version;

    public ValueTask<(bool Success, V? Value, long Version)> TryGetValueAsync(K key)
    {
        var success = dictionary.TryGetValue(key, out var value);
        return new((success, value, _version.Value));
    }

    public async ValueTask<(bool Success, long Version)> TryAddAsync(K key, V value, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (dictionary.TryAdd(key, value))
        {
            _version.Value++;
            await WriteStateAsync();
            return (true, _version.Value);
        }

        return (false, _version.Value);
    }

    public async ValueTask<(bool Success, long Version)> RemoveAsync(K key, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        if (dictionary.Remove(key))
        {
            _version.Value++;
            await WriteStateAsync();
            return (true, _version.Value);
        }

        return (false, _version.Value);
    }

    public async ValueTask<(bool Success, long Version)> ClearAsync(long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        dictionary.Clear();
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }

    public async IAsyncEnumerable<(K Key, V Value, long Version)> GetValuesAsync()
    {
        await Task.CompletedTask; // Make C# happy.
        foreach (var kvp in dictionary)
        {
            yield return (kvp.Key, kvp.Value, _version.Value);
        }
    }

    public async ValueTask<(bool Success, long Version)> SetAsync(K key, V value, long version)
    {
        if (_version.Value != version)
        {
            // Conflict
            return (false, _version.Value);
        }

        dictionary[key] = value;
        _version.Value++;
        await WriteStateAsync();
        return (true, _version.Value);
    }
}
