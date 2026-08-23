using Orleans.Storage;

namespace Orleans.Persistence.FileStorage.Tests;

internal sealed class RecordingGrainStorageSerializer(byte[] serializedBytes, object? reconstructedState)
    : IGrainStorageSerializer
{
    public int DeserializeCallCount { get; private set; }

    public byte[]? DeserializedBytes { get; private set; }

    public int SerializeCallCount { get; private set; }

    public object? SerializedValue { get; private set; }

    public T? Deserialize<T>(BinaryData input)
    {
        DeserializeCallCount++;
        DeserializedBytes = input.ToArray();
        return (T?)reconstructedState;
    }

    public BinaryData Serialize<T>(T? input)
    {
        SerializeCallCount++;
        SerializedValue = input;
        return new BinaryData(serializedBytes);
    }
}
