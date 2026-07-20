using System.Diagnostics.CodeAnalysis;
using Orleans.Storage;

namespace TestExtensions;

public sealed class NonStreamingGrainStorageSerializer : IGrainStorageSerializer
{
    private readonly IGrainStorageSerializer _inner;

    public NonStreamingGrainStorageSerializer(IGrainStorageSerializer inner) => _inner = inner;

    public BinaryData Serialize<T>([AllowNull] T input) => _inner.Serialize(input);

    [return: MaybeNull]
    public T Deserialize<T>(BinaryData input) => _inner.Deserialize<T>(input);
}
