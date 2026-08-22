// <file_grain_storage_options>
using Orleans.Runtime;
using Orleans.Storage;

namespace GrainStorage;

public sealed class FileGrainStorageOptions : IStorageProviderSerializerOptions
{
    public TimeSpan LockAcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public required string RootDirectory { get; set; }

    public required IGrainStorageSerializer GrainStorageSerializer { get; set; }
}

internal sealed class FileGrainStorageOptionsValidator(
    FileGrainStorageOptions options,
    string name) : IConfigurationValidator
{
    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(FileGrainStorage)} with name {name}. " +
                $"{nameof(FileGrainStorageOptions)}.{nameof(FileGrainStorageOptions.RootDirectory)} is required.");
        }

        if (File.Exists(options.RootDirectory))
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(FileGrainStorage)} with name {name}. " +
                $"{nameof(FileGrainStorageOptions)}.{nameof(FileGrainStorageOptions.RootDirectory)} must identify a directory.");
        }

        if (options.LockAcquireTimeout <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException(
                $"Invalid configuration for {nameof(FileGrainStorage)} with name {name}. " +
                $"{nameof(FileGrainStorageOptions)}.{nameof(FileGrainStorageOptions.LockAcquireTimeout)} must be greater than zero.");
        }
    }
}
// </file_grain_storage_options>
