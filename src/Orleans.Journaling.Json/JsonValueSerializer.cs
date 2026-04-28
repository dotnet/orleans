using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Orleans.Journaling.Json;

internal sealed class JsonValueSerializer<T>
{
    private static readonly Type ValueType = typeof(T);
    private readonly JsonTypeInfo<T> _typeInfo;

    public JsonValueSerializer(JsonSerializerOptions? options)
    {
        _typeInfo = GetTypeInfo(options ?? JsonSerializerOptions.Default);
    }

    public void Serialize(Utf8JsonWriter writer, T value) => JsonSerializer.Serialize(writer, value, _typeInfo);

    public T? Deserialize(JsonElement element) => element.Deserialize(_typeInfo);

    private static JsonTypeInfo<T> GetTypeInfo(JsonSerializerOptions options)
    {
        try
        {
            var typeInfo = options.GetTypeInfo(ValueType);
            if (typeInfo is JsonTypeInfo<T> typedTypeInfo)
            {
                return typedTypeInfo;
            }
        }
        catch (NotSupportedException exception)
        {
            throw CreateMissingMetadataException(exception);
        }
        catch (InvalidOperationException exception) when (options.TypeInfoResolver is null)
        {
            throw CreateMissingMetadataException(exception);
        }

        throw CreateMissingMetadataException();
    }

    private static InvalidOperationException CreateMissingMetadataException(Exception? innerException = null)
        => new(
            $"JSON journaling requires System.Text.Json metadata for journaled payload type '{ValueType.FullName}'. "
            + $"Configure {nameof(JsonJournalingOptions)}.{nameof(JsonJournalingOptions.SerializerOptions)} with a source-generated "
            + $"JsonSerializerContext or {nameof(JsonSerializerOptions.TypeInfoResolver)} that includes this type.",
            innerException);
}
