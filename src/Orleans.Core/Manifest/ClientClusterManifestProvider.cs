#nullable enable
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
        private Task? _runTask;

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
            LocalGrainManifest = clientManifestProvider.ClientManifest;

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
                SiloAddress? gateway = null;
                IClusterManifestSystemTarget? provider = null;
                var minorVersion = 0;
                var gatewayVersion = MajorMinorVersion.MinValue;
                while (!_cancellation.IsCancellationRequested)
                {
                    // Select a new gateway if the current one is not available.
                    // This could be caused by a temporary issue or a permanent gateway failure.
                    if (gateway is null || !_gatewayManager.IsGatewayAvailable(gateway))
                    {
                        gateway = _gatewayManager.GetLiveGateway();
                        if (gateway is null)
                        {
                            await Task.Delay(StandardExtensions.Min(_typeManagementOptions.TypeMapRefreshInterval, TimeSpan.FromMilliseconds(500)));
                            continue;
                        }

                        provider = grainFactory.GetGrain<IClusterManifestSystemTarget>(SystemTargetGrainId.Create(Constants.ManifestProviderType, gateway).GrainId);

                        // Accept any cluster manifest version from the new gateway.
                        // Since the minor version of the manifest is specific to each gateway, we reset it to the lowest possible value.
                        // This means that it is possible to receive the an older or equivalent cluster manifest when the gateway changes.
                        // That hiccup is addressed by resetting the expected manifest version and merging incomplete manifests until a complete
                        // manifest is received.
                        gatewayVersion = MajorMinorVersion.MinValue;
                    }

                    Debug.Assert(provider is not null);

                    try
                    {
                        var refreshTask = GetClusterManifestUpdate(provider, gatewayVersion);
                        var task = await Task.WhenAny(cancellationTask, refreshTask).ConfigureAwait(false);

                        if (ReferenceEquals(task, cancellationTask))
                        {
                            return;
                        }

                        var updateResult = await refreshTask;
                        if (updateResult is null)
                        {
                            // There was no newer cluster manifest, so wait for the next refresh interval and try again.
                            await Task.WhenAny(cancellationTask, Task.Delay(_typeManagementOptions.TypeMapRefreshInterval));
                            continue;
                        }

                        gatewayVersion = updateResult.Version;

                        // If the manifest does not contain all active servers, merge with the existing manifest until it does.
                        // This prevents reversed progress at the expense of including potentially defunct silos.
                        ImmutableDictionary<SiloAddress, GrainManifest> siloManifests;
                        if (!updateResult.IncludesAllActiveServers)
                        {
                            // Merge manifests until the manifest contains all active servers.
                            var mergedSilos = _current.Silos.ToBuilder();
                            mergedSilos.Add(_localClientDetails.ClientAddress, LocalGrainManifest);
                            foreach (var kvp in updateResult.SiloManifests)
                            {
                                mergedSilos[kvp.Key] = kvp.Value;
                            }

                            siloManifests = mergedSilos.ToImmutable();
                        }
                        else
                        {
                            siloManifests = updateResult.SiloManifests.Add(_localClientDetails.ClientAddress, LocalGrainManifest);
                        }

                        var updatedManifest = new ClusterManifest(new MajorMinorVersion(gatewayVersion.Major, ++minorVersion), siloManifests);
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

                        // Reset the gateway so that another will be selected on the next iteration.
                        gateway = null;
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

        private async Task<ClusterManifestUpdate?> GetClusterManifestUpdate(IClusterManifestSystemTarget provider, MajorMinorVersion previousVersion)
        {
            try
            {
                // First, attempt to call the new API, which provides more information.
                // This returns null if there is no newer cluster manifest.
                return await provider.GetClusterManifestUpdate(previousVersion);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to fetch cluster manifest update from {Provider}.", provider);

                // If the provider does not support the new API, fall back to the old one.
                var manifest = await provider.GetClusterManifest();
                var result = new ClusterManifestUpdate(manifest.Version, manifest.Silos, includesAllActiveServers: true);
                return result;
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
