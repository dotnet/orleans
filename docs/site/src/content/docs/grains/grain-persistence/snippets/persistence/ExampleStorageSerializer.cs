using System.Text.Json;
using Orleans.Storage;

namespace Orleans.Docs.Snippets.Persistence;

public sealed class ExampleStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T? input) =>
        new(JsonSerializer.SerializeToUtf8Bytes(input));

    public T? Deserialize<T>(BinaryData input) =>
        JsonSerializer.Deserialize<T>(input.ToMemory().Span);
}
