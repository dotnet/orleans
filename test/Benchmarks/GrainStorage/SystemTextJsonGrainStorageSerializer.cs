using System.Text.Json;
using Orleans.Storage;

namespace Benchmarks.GrainStorage;

public sealed class SystemTextJsonGrainStorageSerializer : IGrainStorageSerializer, IGrainStorageStreamingSerializer
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonGrainStorageSerializer(JsonSerializerOptions options = null)
    {
        _options = options ?? new JsonSerializerOptions();
    }

    public BinaryData Serialize<T>(T input)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(input, _options);
        return new BinaryData(payload);
    }

    public T Deserialize<T>(BinaryData input)
    {
        var result = JsonSerializer.Deserialize<T>(input.ToMemory().Span, _options);
        return result!;
    }

    public ValueTask SerializeAsync<T>(T input, Stream destination, CancellationToken cancellationToken = default)
    {
        return new(JsonSerializer.SerializeAsync(destination, input, _options, cancellationToken));
    }

    public async ValueTask<T> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
    {
        var result = await JsonSerializer.DeserializeAsync<T>(input, _options, cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
