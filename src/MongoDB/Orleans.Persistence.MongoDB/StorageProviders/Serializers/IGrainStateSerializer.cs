using MongoDB.Bson;

namespace Orleans.Providers.MongoDB.StorageProviders.Serializers;

/// <summary>
/// Common interface for grain state serializers.
/// </summary>
public interface IGrainStateSerializer
{
    /// <summary>
    /// Serializes the state.
    /// </summary>
    /// <param name="state">The state to serialize.</param>
    /// <typeparam name="T">The input type.</typeparam>
    /// <returns>The serialized value.</returns>
    BsonValue Serialize<T>(T state);

    /// <summary>
    /// Deserializes the provided value.
    /// </summary>
    /// <param name="value">The value to deserialize.</param>
    /// <typeparam name="T">The output type.</typeparam>
    /// <returns>The deserialized state.</returns>
    T Deserialize<T>(BsonValue value);
}
