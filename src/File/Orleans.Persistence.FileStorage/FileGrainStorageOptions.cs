using Orleans.Storage;

namespace Orleans.Persistence.FileStorage;

public sealed class FileGrainStorageOptions : IStorageProviderSerializerOptions
{
    #region properties
    public required string RootDirectory { get; set; }

    public required IGrainStorageSerializer GrainStorageSerializer { get; set; }
    #endregion
}
