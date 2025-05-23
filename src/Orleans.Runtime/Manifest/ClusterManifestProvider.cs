#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.Metadata
{
    internal partial class ClusterManifestProvider : IClusterManifestProvider, IAsyncDisposable, IDisposable, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly SiloAddress _localSiloAddress;
        private readonly ILogger<ClusterManifestProvider> _logger;
        private readonly IServiceProvider _services;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly IFatalErrorHandler _fatalErrorHandler;
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly AsyncEnumerable<ClusterManifest> _updates;
        private ClusterManifest _current;
        private IInternalGrainFactory? _grainFactory;
        private Task? _runTask;

        public ClusterManifestProvider(
            ILocalSiloDetails localSiloDetails,
            SiloManifestProvider siloManifestProvider,
            ClusterMembershipService clusterMembershipService,
            IFatalErrorHandler fatalErrorHandler,
            ILogger<ClusterManifestProvider> logger,
            IServiceProvider services)
        {
            _localSiloAddress = localSiloDetails.SiloAddress;
            _logger = logger;
            _services = services;
            _clusterMembershipService = clusterMembershipService;
            _fatalErrorHandler = fatalErrorHandler;
            LocalGrainManifest = siloManifestProvider.SiloManifest;
            _current = new ClusterManifest(
                MajorMinorVersion.Zero,
                ImmutableDictionary.CreateRange([new KeyValuePair<SiloAddress, GrainManifest>(localSiloDetails.SiloAddress, LocalGrainManifest)]));
            _updates = new AsyncEnumerable<ClusterManifest>(
                initialValue: _current,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Interlocked.Exchange(ref _current, update));
        }

        public ClusterManifest Current => _current;

        public IAsyncEnumerable<ClusterManifest> Updates => _updates;

        public GrainManifest LocalGrainManifest { get; }

        private async Task ProcessMembershipUpdates()
        {
            try
            {
                LogDebugStartingToProcessMembershipUpdates();

                var cancellation = _shutdownCts.Token;
                await foreach (var _ in _clusterMembershipService.MembershipUpdates.WithCancellation(cancellation))
                {
                    while (true)
                    {
                        var membershipSnapshot = _clusterMembershipService.CurrentSnapshot;

                        var success = await UpdateManifest(membershipSnapshot);

                        if (success || cancellation.IsCancellationRequested)
                        {
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                // Ignore during shutdown.
            }
            catch (Exception exception) when (_fatalErrorHandler.IsUnexpected(exception))
            {
                _fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                LogDebugStoppedProcessingMembershipUpdates();
            }
        }

        private async Task<bool> UpdateManifest(ClusterMembershipSnapshot clusterMembership)
        {
            var existingManifest = _current;
            var builder = existingManifest.Silos.ToBuilder();
            var modified = false;

            // First, remove defunct entries.
            foreach (var entry in existingManifest.Silos)
            {
                var address = entry.Key;
                var status = clusterMembership.GetSiloStatus(address);

                if (address.Equals(_localSiloAddress))
                {
                    // The local silo is always present in the manifest.
                    continue;
                }

                if (status == SiloStatus.None || status == SiloStatus.Dead)
                {
                    builder.Remove(address);
                    modified = true;
                }
            }

            // Next, fill missing entries.
            var tasks = new List<Task<(SiloAddress Key, GrainManifest? Value, Exception? Exception)>>();
            foreach (var entry in clusterMembership.Members)
            {
                var member = entry.Value;

                if (member.SiloAddress.Equals(_localSiloAddress))
                {
                    // The local silo is always present in the manifest.
                    continue;
                }

                if (existingManifest.Silos.ContainsKey(member.SiloAddress))
                {
                    // Manifest has already been retrieved for the cluster member.
                    continue;
                }

                if (member.Status != SiloStatus.Active)
                {
                    // If the member is not yet active, it may not be ready to process requests.
                    continue;
                }

                tasks.Add(GetManifest(member.SiloAddress));

                async Task<(SiloAddress, GrainManifest?, Exception?)> GetManifest(SiloAddress siloAddress)
                {
                    try
                    {
                        // Get the manifest from the remote silo.
                        var remoteManifestProvider = _grainFactory!.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, member.SiloAddress);
                        var manifest = await remoteManifestProvider.GetSiloManifest().AsTask().WaitAsync(_shutdownCts.Token);
                        return (siloAddress, manifest, null);
                    }
                    catch (Exception exception)
                    {
                        return (siloAddress, null, exception);
                    }
                }
            }

            var fetchSuccess = true;
            await Task.WhenAll(tasks);
            foreach (var task in tasks)
            {
                var result = await task;
                if (result.Exception is Exception exception)
                {
                    fetchSuccess = false;
                    if (exception is not OperationCanceledException)
                    {
                        LogWarningErrorRetrievingSiloManifest(exception, result.Key);
                    }
                }
                else
                {
                    modified = true;
                    builder[result.Key] = result.Value;
                }
            }

            // Regardless of success or failure, update the manifest if it has been modified.
            var version = new MajorMinorVersion(clusterMembership.Version.Value, existingManifest.Version.Minor + 1);
            if (modified)
            {
                return _updates.TryPublish(new ClusterManifest(version, builder.ToImmutable())) && fetchSuccess;
            }

            return fetchSuccess;
        }

        [MemberNotNull(nameof(_runTask))]
        private Task StartAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_grainFactory is not null);
            _runTask = Task.Run(ProcessMembershipUpdates);
            return Task.CompletedTask;
        }

        [MemberNotNull(nameof(_grainFactory))]
        private Task Initialize(CancellationToken cancellationToken)
        {
            _grainFactory = _services.GetRequiredService<IInternalGrainFactory>();
            return Task.CompletedTask;
        }

        private async Task StopAsync(CancellationToken cancellationToken)
        {
            _shutdownCts.Cancel();
            if (_runTask is Task task)
            {
                await task.WaitAsync(cancellationToken).SuppressThrowing();
            }
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ClusterManifestProvider),
                ServiceLifecycleStage.RuntimeServices,
                Initialize,
                _ => Task.CompletedTask);

            lifecycle.Subscribe(
                nameof(ClusterManifestProvider),
                ServiceLifecycleStage.RuntimeGrainServices,
                StartAsync,
                StopAsync);
        }

        public async ValueTask DisposeAsync()
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                return;
            }

            await StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Error retrieving silo manifest for silo {SiloAddress}"
        )]
        private partial void LogWarningErrorRetrievingSiloManifest(Exception exception, SiloAddress siloAddress);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting to process membership updates"
        )]
        private partial void LogDebugStartingToProcessMembershipUpdates();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Stopped processing membership updates"
        )]
        private partial void LogDebugStoppedProcessingMembershipUpdates();
    }
}
