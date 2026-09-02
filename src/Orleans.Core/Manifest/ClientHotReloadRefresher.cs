using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Runtime.Versions;
using Orleans.Serialization.Hosting;

namespace Orleans.Runtime;

/// <summary>
/// Refreshes an external client's grain metadata after the serialization layer has re-read a hot reload
/// update: re-derives <see cref="GrainTypeOptions"/>, rebuilds the client manifest, and refreshes proxy
/// and resolver caches. Silo manifests arrive through the client's periodic gateway refresh.
/// </summary>
internal sealed partial class ClientHotReloadRefresher : IHotReloadRefreshParticipant, ILifecycleParticipant<IClusterClientLifecycle>, IDisposable
{
    private readonly object _lock = new();
    private readonly SerializationHotReloadRefresher _serializationRefresher;
    private readonly IEnumerable<IConfigureOptions<GrainTypeOptions>> _grainTypeOptionsProviders;
    private readonly GrainTypeOptions _grainTypeOptions;
    private readonly ClientManifestProvider _clientManifestProvider;
    private readonly ClientClusterManifestProvider _clientClusterManifestProvider;
    private readonly RpcProvider _rpcProvider;
    private readonly GrainInterfaceTypeToGrainTypeResolver _grainInterfaceTypeToGrainTypeResolver;
    private readonly GrainVersionManifest _grainVersionManifest;
    private readonly ILogger<ClientHotReloadRefresher> _logger;

    public ClientHotReloadRefresher(
        SerializationHotReloadRefresher serializationRefresher,
        IEnumerable<IConfigureOptions<GrainTypeOptions>> grainTypeOptionsProviders,
        IOptions<GrainTypeOptions> grainTypeOptions,
        ClientManifestProvider clientManifestProvider,
        ClientClusterManifestProvider clientClusterManifestProvider,
        RpcProvider rpcProvider,
        GrainInterfaceTypeToGrainTypeResolver grainInterfaceTypeToGrainTypeResolver,
        GrainVersionManifest grainVersionManifest,
        ILogger<ClientHotReloadRefresher> logger)
    {
        _serializationRefresher = serializationRefresher;
        _grainTypeOptionsProviders = grainTypeOptionsProviders;
        _grainTypeOptions = grainTypeOptions.Value;
        _clientManifestProvider = clientManifestProvider;
        _clientClusterManifestProvider = clientClusterManifestProvider;
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
                foreach (var provider in _grainTypeOptionsProviders)
                {
                    if (provider is DefaultGrainTypeOptionsProvider defaultProvider)
                    {
                        defaultProvider.Configure(_grainTypeOptions);
                    }
                }

                _clientManifestProvider.OnManifestUpdated();
                _clientClusterManifestProvider.OnLocalManifestUpdated(_clientManifestProvider.ClientManifest);
                _rpcProvider.OnManifestUpdated();
                _grainInterfaceTypeToGrainTypeResolver.OnManifestUpdated();
                _grainVersionManifest.OnLocalManifestUpdated(_clientManifestProvider.ClientManifest);
                LogRefreshed();
            }
            catch (Exception exception)
            {
                LogRefreshFailed(exception);
            }
        }
    }

    private void OnSerializationRefreshFailed(string message, Exception exception) => LogSerializationRefreshFailed(exception, message);

    void ILifecycleParticipant<IClusterClientLifecycle>.Participate(IClusterClientLifecycle lifecycle)
    {
        // Construction (triggered by lifecycle participant enumeration) is all that is required.
    }

    public void Dispose()
    {
        _serializationRefresher.RefreshFailed -= OnSerializationRefreshFailed;
        HotReloadMetadataUpdateHandler.Unregister(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed client grain metadata after a hot reload update; newly added grain interfaces are now callable from this client.")]
    private partial void LogRefreshed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh client grain metadata after a hot reload update; a restart may be required for the changes to take effect.")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hot reload serialization refresh reported a failure: {Message}")]
    private partial void LogSerializationRefreshFailed(Exception exception, string message);
}
