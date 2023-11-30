using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
        private readonly LocalClientDetails _localClientDetails;
        private readonly GatewayManager _gatewayManager;
        private readonly AsyncEnumerable<ClusterManifest> _updates;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private ClusterManifest _current;
        private Task _runTask;

        public ClientClusterManifestProvider(
            IServiceProvider services,
            LocalClientDetails localClientDetails,
            GatewayManager gatewayManager,
            ILogger<ClientClusterManifestProvider> logger,
            ClientManifestProvider clientManifestProvider,
            IOptions<TypeManagementOptions> typeManagementOptions)
        {
            _logger = logger;
            _typeManagementOptions = typeManagementOptions.Value;
            _services = services;
            _localClientDetails = localClientDetails;
            _gatewayManager = gatewayManager;
            this.LocalGrainManifest = clientManifestProvider.ClientManifest;

            // Create a fake manifest for the very first generation, which only includes the local client's manifest.
            var builder = ImmutableDictionary.CreateBuilder<SiloAddress, GrainManifest>();
            builder.Add(_localClientDetails.ClientAddress, LocalGrainManifest);
            _current = new ClusterManifest(MajorMinorVersion.MinValue, builder.ToImmutable());

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
                SiloAddress gateway = null;
                IClusterManifestSystemTarget provider = null;
                var minorVersion = 0;
                var gatewayVersion = _current.Version;
                var mergeUntilMajorVersion = _current.Version.Major;
                while (!_cancellation.IsCancellationRequested)
                {
                    // Select a new gateway if the current one is not available.
                    if (gateway is null || !_gatewayManager.IsGatewayAvailable(gateway))
                    {
                        if (gateway is not null)
                        {
                            // Merge manifests until the major version advances.
                            mergeUntilMajorVersion = gatewayVersion.Major + 1;
                        }

                        gateway = _gatewayManager.GetLiveGateway();
                        provider = grainFactory.GetGrain<IClusterManifestSystemTarget>(SystemTargetGrainId.Create(Constants.ManifestProviderType, gateway).GrainId);

                        // Accept any cluster manifest version from the new gateway as long as it at least matches the current major version.
                        // The major version corresponds to the (monotonically increasing) cluster membership version.
                        // Since the minor version is not global, we need to reset it to the lowest possible value.
                        // This means that it is possible to receive the an older or equivalent cluster manifest when the gateway changes.
                        // That hiccup is addressed by merging the newly received manifest with the current one until the major version advances.
                        gatewayVersion = new MajorMinorVersion(_current.Version.Major, long.MinValue);
                    }

                    Debug.Assert(provider is not null);

                    try
                    {
                        var refreshTask = provider.GetClusterManifestIfNewer(gatewayVersion).AsTask();
                        var task = await Task.WhenAny(cancellationTask, refreshTask).ConfigureAwait(false);

                        if (ReferenceEquals(task, cancellationTask))
                        {
                            return;
                        }

                        var gatewayManifest = await refreshTask;
                        if (gatewayManifest is null)
                        {
                            // There was no newer cluster manifest, so wait for the next refresh interval and try again.
                            await Task.WhenAny(cancellationTask, Task.Delay(_typeManagementOptions.TypeMapRefreshInterval));
                            continue;
                        }

                        gatewayVersion = gatewayManifest.Version;

                        // If the client switched to a new gateway, we need to merge the manifests.
                        ImmutableDictionary<SiloAddress, GrainManifest> siloManifests;
                        if (mergeUntilMajorVersion > gatewayManifest.Version.Major)
                        {
                            // Merge manifests until the major version advances.
                            var mergedSilos = _current.Silos.ToBuilder();
                            mergedSilos.Add(_localClientDetails.ClientAddress, LocalGrainManifest);
                            foreach (var kvp in gatewayManifest.Silos)
                            {
                                mergedSilos[kvp.Key] = kvp.Value;
                            }

                            siloManifests = mergedSilos.ToImmutable();
                        }
                        else
                        {
                            siloManifests = gatewayManifest.Silos.Add(_localClientDetails.ClientAddress, LocalGrainManifest);
                        }

                        var updatedManifest = new ClusterManifest(new MajorMinorVersion(gatewayVersion.Major, ++minorVersion), gatewayManifest.Silos);
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
