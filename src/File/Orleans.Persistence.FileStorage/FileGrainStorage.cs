using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.FileStorage;

public sealed class FileGrainStorage(
    string storageName,
    FileGrainStorageOptions options,
    IOptions<ClusterOptions> clusterOptions) : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    #region fields
    private readonly ClusterOptions _clusterOptions = clusterOptions.Value;
    #endregion

    #region IGrainStorage
    public Task ClearStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var fName = GetKeyString(stateName, grainId);
        var path = Path.Combine(options.RootDirectory, fName!);
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists)
        {
            if (fileInfo.LastWriteTimeUtc.ToString(CultureInfo.InvariantCulture) != grainState.ETag)
            {
                throw new InconsistentStateException($"""
                    Version conflict (ClearState): ServiceId={_clusterOptions.ServiceId}
                    ProviderName={storageName} GrainType={typeof(T)}
                    GrainReference={grainId}.
                    """);
            }

            grainState.ETag = null;
            grainState.State = Activator.CreateInstance<T>()!;

            fileInfo.Delete();
        }

        return Task.CompletedTask;
    }
    public async Task ReadStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var fName = GetKeyString(stateName, grainId);
        var path = Path.Combine(options.RootDirectory, fName!);
        var fileInfo = new FileInfo(path);
        if (fileInfo is { Exists: false })
        {
            grainState.State = Activator.CreateInstance<T>()!;
            return;
        }

        using var stream = fileInfo.OpenText();
        var storedData = await stream.ReadToEndAsync();

        grainState.State = options.GrainStorageSerializer.Deserialize<T>(new BinaryData(storedData));
        grainState.ETag = fileInfo.LastWriteTimeUtc.ToString(CultureInfo.InvariantCulture);
        grainState.RecordExists = true;
    }
    public async Task WriteStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var storedData = options.GrainStorageSerializer.Serialize(grainState.State);
        var fName = GetKeyString(stateName, grainId);
        var path = Path.Combine(options.RootDirectory, fName!);
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists && fileInfo.LastWriteTimeUtc.ToString(CultureInfo.InvariantCulture) != grainState.ETag)
        {
            throw new InconsistentStateException($"""
                Version conflict (WriteState): ServiceId={_clusterOptions.ServiceId}
                ProviderName={storageName} GrainType={typeof(T)}
                GrainReference={grainId}.
                """);
        }

        await File.WriteAllBytesAsync(path, storedData.ToArray());

        fileInfo.Refresh();
        grainState.ETag = fileInfo.LastWriteTimeUtc.ToString(CultureInfo.InvariantCulture);
    }
    #endregion

    #region ILifecycleParticipant<ISiloLifecycle>
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(
            observerName: OptionFormattingUtilities.Name<FileGrainStorage>(storageName),
            stage: ServiceLifecycleStage.ApplicationServices,
            onStart: (ct) =>
            {
                Directory.CreateDirectory(options.RootDirectory);
                return Task.CompletedTask;
            });
    #endregion

    #region helpers
    private string GetKeyString(string grainType, GrainId grainId) => $"{_clusterOptions.ServiceId}.{grainId.Key}.{grainType}.STATE";
    #endregion
}
