using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage;

namespace GrainStorage;

public sealed class FileGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly string _storageName;
    private readonly FileGrainStorageOptions _options;
    private readonly ClusterOptions _clusterOptions;

    public FileGrainStorage(
        string storageName,
        FileGrainStorageOptions options,
        IOptions<ClusterOptions> clusterOptions)
    {
        _storageName = storageName;
        _options = options;
        _clusterOptions = clusterOptions.Value;
    }

    // <clearstateasync>
    public Task ClearStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var fName = GetKeyString(stateName, grainId);
        var path = Path.Combine(_options.RootDirectory, fName!);
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists)
        {
            if (fileInfo.LastWriteTimeUtc.ToString() != grainState.ETag)
            {
                throw new InconsistentStateException($"""
                    Version conflict (ClearState): ServiceId={_clusterOptions.ServiceId}
                    ProviderName={_storageName} GrainType={typeof(T)}
                    GrainReference={grainId}.
                    """);
            }

            grainState.ETag = null;
            grainState.State = (T)Activator.CreateInstance(typeof(T))!;

            fileInfo.Delete();
        }

        return Task.CompletedTask;
    }
    // </clearstateasync>
    // <readstateasync>
    public async Task ReadStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var fName = GetKeyString(stateName, grainId);
        var path = Path.Combine(_options.RootDirectory, fName!);
        var fileInfo = new FileInfo(path);
        if (fileInfo is { Exists: false })
        {
            grainState.State = (T)Activator.CreateInstance(typeof(T))!;
            return;
        }

        using var stream = fileInfo.OpenText();
        var storedData = await stream.ReadToEndAsync();
        
        grainState.State = _options.GrainStorageSerializer.Deserialize<T>(new BinaryData(storedData));
        grainState.ETag = fileInfo.LastWriteTimeUtc.ToString();
    }
    // </readstateasync>
    // <writestateasync>
    public async Task WriteStateAsync<T>(
        string stateName,
        GrainId grainId,
        IGrainState<T> grainState)
    {
        var storedData = _options.GrainStorageSerializer.Serialize(grainState.State);
        var fName = GetKeyString(stateName, grainId);
        var path = Path.Combine(_options.RootDirectory, fName!);
        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists && fileInfo.LastWriteTimeUtc.ToString() != grainState.ETag)
        {
            throw new InconsistentStateException($"""
                Version conflict (WriteState): ServiceId={_clusterOptions.ServiceId}
                ProviderName={_storageName} GrainType={typeof(T)}
                GrainReference={grainId}.
                """);
        }

        await File.WriteAllBytesAsync(path, storedData.ToArray());

        fileInfo.Refresh();
        grainState.ETag = fileInfo.LastWriteTimeUtc.ToString();
    }
    // </writestateasync>
    // <participate>
    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(
            observerName: OptionFormattingUtilities.Name<FileGrainStorage>(_storageName),
            stage: ServiceLifecycleStage.ApplicationServices,
            onStart: (ct) =>
            {
                Directory.CreateDirectory(_options.RootDirectory);
                return Task.CompletedTask;
            });
    // </participate>
    // <getkeystring>
    private string GetKeyString(string grainType, GrainId grainId) =>
        $"{_clusterOptions.ServiceId}.{grainId.Key}.{grainType}";
    // </getkeystring>
}
