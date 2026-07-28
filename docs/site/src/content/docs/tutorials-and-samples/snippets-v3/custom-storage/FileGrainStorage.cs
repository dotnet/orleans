using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace GrainStorage;

// <file_grain_storage>
public class FileGrainStorage
    : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly string _storageName;
    private readonly FileGrainStorageOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly IGrainFactory _grainFactory;
    private readonly ITypeResolver _typeResolver;
    private JsonSerializerSettings _jsonSettings;

    public FileGrainStorage(
        string storageName,
        FileGrainStorageOptions options,
        IOptions<ClusterOptions> clusterOptions,
        IGrainFactory grainFactory,
        ITypeResolver typeResolver)
    {
        _storageName = storageName;
        _options = options;
        _clusterOptions = clusterOptions.Value;
        _grainFactory = grainFactory;
        _typeResolver = typeResolver;
    }

    public Task ClearStateAsync(
        string grainType,
        GrainReference grainReference,
        IGrainState grainState)
    {
        throw new NotImplementedException();
    }

    public Task ReadStateAsync(
        string grainType,
        GrainReference grainReference,
        IGrainState grainState)
    {
        throw new NotImplementedException();
    }

    public Task WriteStateAsync(
        string grainType,
        GrainReference grainReference,
        IGrainState grainState)
    {
        throw new NotImplementedException();
    }

    public void Participate(
        ISiloLifecycle lifecycle)
    {
        throw new NotImplementedException();
    }
}
// </file_grain_storage>

public class FileGrainStorageImpl
    : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly string _storageName;
    private readonly FileGrainStorageOptions _options;
    private readonly ClusterOptions _clusterOptions;
    private readonly IGrainFactory _grainFactory;
    private readonly ITypeResolver _typeResolver;
    private JsonSerializerSettings _jsonSettings;

    public FileGrainStorageImpl(
        string storageName,
        FileGrainStorageOptions options,
        IOptions<ClusterOptions> clusterOptions,
        IGrainFactory grainFactory,
        ITypeResolver typeResolver)
    {
        _storageName = storageName;
        _options = options;
        _clusterOptions = clusterOptions.Value;
        _grainFactory = grainFactory;
        _typeResolver = typeResolver;
    }

    // <participate>
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            OptionFormattingUtilities.Name<FileGrainStorageImpl>(_storageName),
            ServiceLifecycleStage.ApplicationServices,
            Init);
    }
    // </participate>

    // <init>
    private Task Init(CancellationToken ct)
    {
        // Settings could be made configurable from Options.
        _jsonSettings =
            OrleansJsonSerializer.UpdateSerializerSettings(
                OrleansJsonSerializer.GetDefaultSerializerSettings(
                    _typeResolver,
                    _grainFactory),
                false,
                false,
                null);

        var directory = new DirectoryInfo(_options.RootDirectory);
        if (!directory.Exists)
            directory.Create();

        return Task.CompletedTask;
    }
    // </init>

    // <getkeystring>
    private string GetKeyString(string grainType, GrainReference grainReference)
    {
        return $"{_clusterOptions.ServiceId}.{grainReference.ToKeyString()}.{grainType}";
    }
    // </getkeystring>

    // <readstateasync>
    public async Task ReadStateAsync(
        string grainType,
        GrainReference grainReference,
        IGrainState grainState)
    {
        var fName = GetKeyString(grainType, grainReference);
        var path = Path.Combine(_options.RootDirectory, fName);

        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            grainState.State = Activator.CreateInstance(grainState.State.GetType());
            return;
        }

        using (var stream = fileInfo.OpenText())
        {
            var storedData = await stream.ReadToEndAsync();
            grainState.State = JsonConvert.DeserializeObject(storedData, _jsonSettings);
        }

        grainState.ETag = fileInfo.LastWriteTimeUtc.ToString();
    }
    // </readstateasync>

    // <writestateasync>
    public async Task WriteStateAsync(
        string grainType,
        GrainReference grainReference,
        IGrainState grainState)
    {
        var storedData = JsonConvert.SerializeObject(grainState.State, _jsonSettings);

        var fName = GetKeyString(grainType, grainReference);
        var path = Path.Combine(_options.RootDirectory, fName);

        var fileInfo = new FileInfo(path);

        if (fileInfo.Exists && fileInfo.LastWriteTimeUtc.ToString() != grainState.ETag)
        {
            throw new InconsistentStateException(
                $"Version conflict (WriteState): ServiceId={_clusterOptions.ServiceId} " +
                $"ProviderName={_storageName} GrainType={grainType} " +
                $"GrainReference={grainReference.ToKeyString()}.");
        }

        using (var stream = new StreamWriter(fileInfo.Open(FileMode.Create, FileAccess.Write)))
        {
            await stream.WriteAsync(storedData);
        }

        fileInfo.Refresh();
        grainState.ETag = fileInfo.LastWriteTimeUtc.ToString();
    }
    // </writestateasync>

    // <clearstateasync>
    public Task ClearStateAsync(
        string grainType,
        GrainReference grainReference,
        IGrainState grainState)
    {
        var fName = GetKeyString(grainType, grainReference);
        var path = Path.Combine(_options.RootDirectory, fName);

        var fileInfo = new FileInfo(path);
        if (fileInfo.Exists)
        {
            if (fileInfo.LastWriteTimeUtc.ToString() != grainState.ETag)
            {
                throw new InconsistentStateException(
                    $"Version conflict (ClearState): ServiceId={_clusterOptions.ServiceId} " +
                    $"ProviderName={_storageName} GrainType={grainType} " +
                    $"GrainReference={grainReference.ToKeyString()}.");
            }

            grainState.ETag = null;
            grainState.State = Activator.CreateInstance(grainState.State.GetType());
            fileInfo.Delete();
        }

        return Task.CompletedTask;
    }
    // </clearstateasync>
}
