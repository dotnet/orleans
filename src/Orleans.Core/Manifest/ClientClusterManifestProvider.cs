using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime
{
    /// <summary>
    /// <see cref="IClusterManifestProvider"/> implementation for external clients.
    /// </summary>
    internal class ClientClusterManifestProvider : IClusterManifestProvider, IAsyncDisposable, IDisposable
    {
        private readonly TaskCompletionSource<bool> _initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ILogger<ClientClusterManifestProvider> _logger;
        private readonly TypeManagementOptions _typeManagementOptions;
        private readonly IServiceProvider _services;
        private readonly GatewayManager _gatewayManager;
        private readonly AsyncEnumerable<ClusterManifest> _updates;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private ClusterManifest _current;
        private Task _runTask;
        private ConcurrentDictionary<SiloAddress, MajorMinorVersion> siloAddressVersionMap = new ConcurrentDictionary<SiloAddress, MajorMinorVersion>();

        public ClientClusterManifestProvider(
            IServiceProvider services,
            GatewayManager gatewayManager,
            ILogger<ClientClusterManifestProvider> logger,
            ClientManifestProvider clientManifestProvider,
            IOptions<TypeManagementOptions> typeManagementOptions)
        {
            _logger = logger;
            _typeManagementOptions = typeManagementOptions.Value;
            _services = services;
            _gatewayManager = gatewayManager;
            this.LocalGrainManifest = clientManifestProvider.ClientManifest;
            _current = new ClusterManifest(MajorMinorVersion.MinValue, ImmutableDictionary<SiloAddress, GrainManifest>.Empty, ImmutableArray.Create(this.LocalGrainManifest));
            _updates = new AsyncEnumerable<ClusterManifest>(
                initialValue: _current,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Interlocked.Exchange(ref _current, update));
        }

        /// <inheritdoc />
        public ClusterManifest Current => _current;

        /// <inheritdoc />
        public IAsyncEnumerable<ClusterManifest> Updates => _updates;

        /// <inheritdoc />
        public GrainManifest LocalGrainManifest { get; }

        /// <summary>
        /// Starts this service.
        /// </summary>
        /// <returns>A <see cref="Task"/> which completes once the service has started.</returns>
        public Task StartAsync()
        {
            _runTask = Task.Run(RunAsync);
            return _initialized.Task;
        }

        private async Task RunAsync()
        {
            try
            {
                var grainFactory = _services.GetRequiredService<IInternalGrainFactory>();
                var cancellationTask = _cancellation.Token.WhenCancelled();
                while (!_cancellation.IsCancellationRequested)
                {
                    var gateway = _gatewayManager.GetLiveGateway();
                    try
                    {
                        var provider = grainFactory.GetGrain<IClusterManifestSystemTarget>(SystemTargetGrainId.Create(Constants.ManifestProviderType, gateway).GrainId);
                        var currentVersion = _current.Version;
                        if (siloAddressVersionMap.ContainsKey(gateway))
                        {
                            siloAddressVersionMap.TryGetValue(gateway, out currentVersion);
                        }
                        var refreshTask = provider.GetClusterManifestIfNewer(currentVersion).AsTask();
                        var task = await Task.WhenAny(cancellationTask, refreshTask).ConfigureAwait(false);

                        if (ReferenceEquals(task, cancellationTask))
                        {
                            return;
                        }

                        var updatedManifest = await refreshTask;
                        if (updatedManifest is null)
                        {
                            // There was no newer cluster manifest, so wait for the next refresh interval and try again.
                            await Task.WhenAny(cancellationTask, Task.Delay(_typeManagementOptions.TypeMapRefreshInterval));
                            continue;
                        }
                        siloAddressVersionMap[gateway] = updatedManifest.Version;

                        if (!_updates.TryPublish(updatedManifest))
                        {
                            await Task.Delay(StandardExtensions.Min(_typeManagementOptions.TypeMapRefreshInterval, TimeSpan.FromMilliseconds(500)));
                            continue;
                        }

                        _initialized.TrySetResult(true);

                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Refreshed cluster manifest");
                        }

                        await Task.WhenAny(cancellationTask, Task.Delay(_typeManagementOptions.TypeMapRefreshInterval));
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Error trying to get cluster manifest from gateway {Gateway}", gateway);
                        await Task.Delay(StandardExtensions.Min(_typeManagementOptions.TypeMapRefreshInterval, TimeSpan.FromSeconds(5)));
                    }
                }
            }
            finally
            {
                _initialized.TrySetResult(false);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Stopped refreshing cluster manifest");
                }
            }
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            return _runTask is Task task ? new ValueTask(task) : default;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cancellation.Cancel();
        }
    }
}
