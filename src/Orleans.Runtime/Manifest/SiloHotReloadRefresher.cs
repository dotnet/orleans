using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.Versions;
using Orleans.Serialization.Hosting;

namespace Orleans.Runtime.Metadata;

/// <summary>
/// Refreshes the silo's grain metadata after the serialization layer has re-read a hot reload update:
/// re-derives <see cref="GrainTypeOptions"/>, rebuilds the silo manifest and grain class map, republishes
/// the cluster manifest with a bumped minor version, and refreshes proxy and resolver caches.
/// </summary>
internal sealed partial class SiloHotReloadRefresher : IHotReloadRefreshParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private readonly object _lock = new();
    private readonly SerializationHotReloadRefresher _serializationRefresher;
    private readonly IEnumerable<IConfigureOptions<GrainTypeOptions>> _grainTypeOptionsProviders;
    private readonly GrainTypeOptions _grainTypeOptions;
    private readonly SiloManifestProvider _siloManifestProvider;
    private readonly ClusterManifestProvider _clusterManifestProvider;
    private readonly RpcProvider _rpcProvider;
    private readonly GrainInterfaceTypeToGrainTypeResolver _grainInterfaceTypeToGrainTypeResolver;
    private readonly GrainVersionManifest _grainVersionManifest;
    private readonly ILogger<SiloHotReloadRefresher> _logger;

    public SiloHotReloadRefresher(
        SerializationHotReloadRefresher serializationRefresher,
        IEnumerable<IConfigureOptions<GrainTypeOptions>> grainTypeOptionsProviders,
        IOptions<GrainTypeOptions> grainTypeOptions,
        SiloManifestProvider siloManifestProvider,
        ClusterManifestProvider clusterManifestProvider,
        RpcProvider rpcProvider,
        GrainInterfaceTypeToGrainTypeResolver grainInterfaceTypeToGrainTypeResolver,
        GrainVersionManifest grainVersionManifest,
        ILogger<SiloHotReloadRefresher> logger)
    {
        _serializationRefresher = serializationRefresher;
        _grainTypeOptionsProviders = grainTypeOptionsProviders;
        _grainTypeOptions = grainTypeOptions.Value;
        _siloManifestProvider = siloManifestProvider;
        _clusterManifestProvider = clusterManifestProvider;
        _rpcProvider = rpcProvider;
        _grainInterfaceTypeToGrainTypeResolver = grainInterfaceTypeToGrainTypeResolver;
        _grainVersionManifest = grainVersionManifest;
        _logger = logger;
        _serializationRefresher.RefreshFailed += OnSerializationRefreshFailed;
        HotReloadMetadataUpdateHandler.Register(this);
    }

    public int Phase => 1;

    public void Refresh(HashSet<Assembly>? updatedAssemblies)
    {
        lock (_lock)
        {
            try
            {
                // Only the default provider is re-run; user-supplied configurators have unknown idempotency.
                foreach (var provider in _grainTypeOptionsProviders)
                {
                    if (provider is DefaultGrainTypeOptionsProvider defaultProvider)
                    {
                        defaultProvider.Configure(_grainTypeOptions);
                    }
                }

                _siloManifestProvider.OnManifestUpdated();
                _clusterManifestProvider.OnLocalManifestUpdated(_siloManifestProvider.SiloManifest);
                _rpcProvider.OnManifestUpdated();
                _grainInterfaceTypeToGrainTypeResolver.OnManifestUpdated();
                _grainVersionManifest.OnLocalManifestUpdated(_siloManifestProvider.SiloManifest);
                LogRefreshed();
            }
            catch (Exception exception)
            {
                LogRefreshFailed(exception);
            }
        }
    }

    private void OnSerializationRefreshFailed(string message, Exception exception) => LogSerializationRefreshFailed(exception, message);

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        // Construction (triggered by lifecycle participant enumeration) is all that is required.
    }

    public void Dispose()
    {
        _serializationRefresher.RefreshFailed -= OnSerializationRefreshFailed;
        HotReloadMetadataUpdateHandler.Unregister(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed grain metadata after a hot reload update; newly added grain types are now activatable on this silo.")]
    private partial void LogRefreshed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh grain metadata after a hot reload update; a restart may be required for the changes to take effect.")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hot reload serialization refresh reported a failure: {Message}")]
    private partial void LogSerializationRefreshFailed(Exception exception, string message);
}
