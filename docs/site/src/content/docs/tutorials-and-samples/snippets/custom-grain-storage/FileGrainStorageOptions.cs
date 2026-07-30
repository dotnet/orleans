using Orleans.Storage;

namespace GrainStorage;

public sealed class FileGrainStorageOptions : IStorageProviderSerializerOptions
{
    public required string RootDirectory { get; set; }

    public required IGrainStorageSerializer GrainStorageSerializer { get; set; }
}
