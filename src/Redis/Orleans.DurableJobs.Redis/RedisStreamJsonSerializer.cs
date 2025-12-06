using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StackExchange.Redis;

namespace Orleans.DurableJobs.Redis;

internal static class RedisStreamJsonSerializer<T>
{
    public static async IAsyncEnumerable<T> DecodeAsync(StreamEntry[] streamEntries, JsonTypeInfo<T> jsonTypeInfo, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (streamEntries is null) yield break;

        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        foreach (var streamEntry in streamEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find the field named "payload" (case-sensitive) - matches the Lua script in RedisJobShard
            var dataField = streamEntry.Values.FirstOrDefault(v => v.Name == "payload");

            if (dataField.Equals(default) || dataField.Value.IsNull)
            {
                // Skip entries without a data field
                continue;
            }

            // Read JSON as string (Redis stores stream field values as binary or string)
            var json = (string?)dataField.Value;
            if (string.IsNullOrEmpty(json))
            {
                continue;
            }

            // Deserialize using provided JsonTypeInfo
            var item = JsonSerializer.Deserialize(json, jsonTypeInfo) ?? throw new JsonException("Deserialized JSON resulted in null value");

            // Yield asynchronously - allow caller to observe cancellation between items
            await Task.Yield();
            yield return item;
        }
    }

    /// <summary>
    /// Serializes a collection of items to JSON strings as RedisValue array using the provided JsonTypeInfo for source-generated serialization.
    /// </summary>
    /// <param name="items">The items to serialize.</param>
    /// <param name="jsonTypeInfo">The JSON type info for source-generated serialization.</param>
    /// <returns>An array of RedisValue containing JSON strings.</returns>
    public static RedisValue[] Encode(IEnumerable<T> items, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        return items.Select(item => (RedisValue)JsonSerializer.Serialize(item, jsonTypeInfo)).ToArray();
    }
}
