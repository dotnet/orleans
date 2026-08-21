using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.FileStorage;

/// <summary>
/// Options for <see cref="FileGrainStorage"/>.
/// </summary>
public sealed class FileGrainStorageOptions : IStorageProviderSerializerOptions
{
    /// <summary>
    /// Gets or sets the maximum time to wait for exclusive access to a storage record.
    /// </summary>
    public TimeSpan LockAcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the directory used to store grain state.
    /// </summary>
    public required string RootDirectory { get; set; }

    /// <inheritdoc />
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
