using Orleans.Storage;

namespace TestExtensions;

public sealed class NonStreamingGrainStorageSerializer : IGrainStorageSerializer
{
    private readonly IGrainStorageSerializer _inner;

    public NonStreamingGrainStorageSerializer(IGrainStorageSerializer inner) => _inner = inner;

    public BinaryData Serialize<T>(T input) => _inner.Serialize(input);

    public T Deserialize<T>(BinaryData input) => _inner.Deserialize<T>(input);
}
