using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core.Diagnostics;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Runtime.Dissemination;
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
#if NET9_0_OR_GREATER
        private readonly Lock _currentLock = new();
#else
        private readonly object _currentLock = new();
#endif
        private ClusterManifest _current;
        private IInternalGrainFactory? _grainFactory;
        private Task? _runTask;
        private readonly Dictionary<ManifestHash, GrainManifest> _manifestCache = new();

        public ClusterManifestProvider(
            ILocalSiloDetails localSiloDetails,
            SiloManifestProvider siloManifestProvider,
            IClusterMembershipService clusterMembershipService,
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
            _manifestCache[ManifestHashCalculator.ComputeHash(LocalGrainManifest)] = LocalGrainManifest;
            _current = CreateClusterManifest(
                MajorMinorVersion.MinValue,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty);
            _updates = new AsyncEnumerable<ClusterManifest>(
                initialValue: _current,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Interlocked.Exchange(ref _current, update));
        }

        public ClusterManifest Current => EnsureValidManifestForCurrentMembership(_clusterMembershipService.CurrentSnapshot);

        public IAsyncEnumerable<ClusterManifest> Updates => _updates;

        public GrainManifest LocalGrainManifest { get; }

        private ClusterManifest EnsureValidManifestForCurrentMembership(ClusterMembershipSnapshot clusterMembership)
        {
            var current = _current;
            var membershipVersion = clusterMembership.Version.Value;
            if (current.Version.Major >= membershipVersion)
            {
                return current;
            }

            lock (_currentLock)
            {
                current = _current;
                if (current.Version.Major >= membershipVersion)
                {
                    return current;
                }

                var synchronizedSilos = RemoveNonActiveSilos(current.Silos, clusterMembership);
                if (clusterMembership.GetSiloStatus(_localSiloAddress) == SiloStatus.Active
                    && !synchronizedSilos.ContainsKey(_localSiloAddress))
                {
                    synchronizedSilos = synchronizedSilos.Add(_localSiloAddress, LocalGrainManifest);
                }

                var version = new MajorMinorVersion(membershipVersion, 0);
                var updated = CreateClusterManifest(version, synchronizedSilos);
                TryPublishManifest(updated);
                return _current;
            }
        }

        private async Task ProcessMembershipUpdates()
        {
            try
            {
                LogDebugStartingToProcessMembershipUpdates();

                var cancellationToken = _shutdownCts.Token;
                await using var membershipUpdates = _clusterMembershipService.MembershipUpdates.GetAsyncEnumerator(cancellationToken);
                var nextUpdateTask = membershipUpdates.MoveNextAsync().AsTask();
                ClusterMembershipSnapshot? membershipSnapshot = null;

                while (true)
                {
                    if (membershipSnapshot is null)
                    {
                        if (!await nextUpdateTask)
                        {
                            return;
                        }

                        membershipSnapshot = membershipUpdates.Current;
                        nextUpdateTask = membershipUpdates.MoveNextAsync().AsTask();
                    }

                    if (await UpdateManifest(membershipSnapshot))
                    {
                        membershipSnapshot = null;
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var retryDelayTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    if (await Task.WhenAny(nextUpdateTask, retryDelayTask) == nextUpdateTask)
                    {
                        if (!await nextUpdateTask)
                        {
                            return;
                        }

                        membershipSnapshot = membershipUpdates.Current;
                        nextUpdateTask = membershipUpdates.MoveNextAsync().AsTask();
                    }
                    else
                    {
                        await retryDelayTask;
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
            var existingManifest = EnsureValidManifestForCurrentMembership(clusterMembership);
            if (existingManifest.Version.Major > clusterMembership.Version.Value)
            {
                return true;
            }

            var builder = existingManifest.Silos.ToBuilder();
            var modified = false;

            // Fill missing entries.
            var missingSilos = new List<SiloAddress>();
            foreach (var entry in clusterMembership.Members)
            {
                var member = entry.Value;
                if (member.Status != SiloStatus.Active)
                {
                    // If the member is not yet active, it may not be ready to process requests.
                    continue;
                }

                var siloAddress = member.SiloAddress;
                if (builder.ContainsKey(siloAddress))
                {
                    // Manifest has already been retrieved for the cluster member.
                    continue;
                }

                missingSilos.Add(siloAddress);
            }

            if (missingSilos.Count > 1 && await TryFillMissingManifestsFromPeers(clusterMembership, existingManifest.Version, builder, missingSilos))
            {
                modified = true;
            }

            var tasks = new List<Task<(SiloAddress Key, GrainManifest? Value, Exception? Exception)>>();
            foreach (var siloAddress in missingSilos)
            {
                if (!builder.ContainsKey(siloAddress))
                {
                    tasks.Add(GetManifest(siloAddress));
                }
            }

            async Task<(SiloAddress Key, GrainManifest? Value, Exception? Exception)> GetManifest(SiloAddress siloAddress)
            {
                try
                {
                    var manifest = await GetSiloManifest(siloAddress);
                    return (siloAddress, manifest, null);
                }
                catch (Exception exception)
                {
                    return (siloAddress, null, exception);
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
                    if (result.Value is not null)
                    {
                        modified = true;
                        _manifestCache[ManifestHashCalculator.ComputeHash(result.Value)] = result.Value;
                        builder[result.Key] = result.Value;
                    }
                    else
                    {
                        fetchSuccess = false;
                    }
                }
            }

            // Regardless of success or failure, update the manifest if it has been modified.
            var version = new MajorMinorVersion(clusterMembership.Version.Value, existingManifest.Version.Minor + 1);
            if (modified)
            {
                var manifest = CreateClusterManifest(version, builder.ToImmutable());
                var publishSuccess = TryPublishManifest(manifest);
                return publishSuccess && fetchSuccess;
            }
            return fetchSuccess;
        }

        private async Task<bool> TryFillMissingManifestsFromPeers(
            ClusterMembershipSnapshot clusterMembership,
            MajorMinorVersion previousVersion,
            ImmutableDictionary<SiloAddress, GrainManifest>.Builder builder,
            List<SiloAddress> missingSilos)
        {
            var missing = new HashSet<SiloAddress>(missingSilos);
            var modified = false;
            foreach (var peer in clusterMembership.Members.Values
                .Where(static member => member.Status == SiloStatus.Active)
                .Select(static member => member.SiloAddress)
                .OrderBy(static silo => silo))
            {
                if (missing.Count == 0)
                {
                    break;
                }

                if (peer.Equals(_localSiloAddress))
                {
                    continue;
                }

                try
                {
                    var remoteManifestProvider = _grainFactory!.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer);
                    var summary = await remoteManifestProvider.GetClusterManifestHashSummary().AsTask().WaitAsync(_shutdownCts.Token);
                    FillFromCachedHashes(summary, missing, builder, ref modified);
                    if (missing.Count == 0)
                    {
                        break;
                    }

                    var update = await remoteManifestProvider.GetClusterManifestUpdate(previousVersion).AsTask().WaitAsync(_shutdownCts.Token);
                    if (update?.SiloManifests is null)
                    {
                        continue;
                    }

                    foreach (var silo in missing.ToArray())
                    {
                        if (!summary.SiloManifestHashes.TryGetValue(silo, out var expectedHash)
                            || !update.SiloManifests.TryGetValue(silo, out var manifest)
                            || ManifestHashCalculator.ComputeHash(manifest) != expectedHash)
                        {
                            continue;
                        }

                        _manifestCache[expectedHash] = manifest;
                        builder[silo] = manifest;
                        missing.Remove(silo);
                        modified = true;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogDebugErrorRetrievingClusterManifestFromPeer(exception, peer);
                }
            }

            return modified;
        }

        private void FillFromCachedHashes(
            ClusterManifestHashSummary summary,
            HashSet<SiloAddress> missing,
            ImmutableDictionary<SiloAddress, GrainManifest>.Builder builder,
            ref bool modified)
        {
            foreach (var silo in missing.ToArray())
            {
                if (summary.SiloManifestHashes.TryGetValue(silo, out var hash)
                    && _manifestCache.TryGetValue(hash, out var cached))
                {
                    builder[silo] = cached;
                    missing.Remove(silo);
                    modified = true;
                }
            }
        }

        private ClusterManifest CreateClusterManifest(
            MajorMinorVersion version,
            ImmutableDictionary<SiloAddress, GrainManifest> silos)
        {
            return new ClusterManifest(version, silos, [LocalGrainManifest]);
        }

        private bool TryPublishManifest(ClusterManifest manifest)
        {
            var publishSuccess = _updates.TryPublish(manifest);
            if (publishSuccess)
            {
                ManifestEvents.EmitClusterManifestUpdated(this, manifest);
            }

            return publishSuccess;
        }

        private static ImmutableDictionary<SiloAddress, GrainManifest> RemoveNonActiveSilos(
            ImmutableDictionary<SiloAddress, GrainManifest> silos,
            ClusterMembershipSnapshot clusterMembership)
        {
            ImmutableDictionary<SiloAddress, GrainManifest>.Builder? builder = null;
            foreach (var entry in silos)
            {
                if (clusterMembership.GetSiloStatus(entry.Key) == SiloStatus.Active)
                {
                    continue;
                }

                builder ??= silos.ToBuilder();
                builder.Remove(entry.Key);
            }

            return builder?.ToImmutable() ?? silos;
        }

        private async Task<GrainManifest> GetSiloManifest(SiloAddress siloAddress)
        {
            try
            {
                var remoteManifestProvider = _grainFactory!.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, siloAddress);
                var hash = await remoteManifestProvider.GetSiloManifestHash().AsTask().WaitAsync(_shutdownCts.Token);
                if (_manifestCache.TryGetValue(hash, out var cached))
                {
                    return cached;
                }

                var manifest = await remoteManifestProvider.GetSiloManifestByHash(hash).AsTask().WaitAsync(_shutdownCts.Token);
                if (manifest is not null && ManifestHashCalculator.ComputeHash(manifest) == hash)
                {
                    _manifestCache[hash] = manifest;
                    return manifest;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogDebugErrorRetrievingSiloManifestByHash(exception, siloAddress);
            }
            var legacyManifestProvider = _grainFactory!.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, siloAddress);
            return await legacyManifestProvider.GetSiloManifest().AsTask().WaitAsync(_shutdownCts.Token);
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
                NoOpStop);

            lifecycle.Subscribe(
                nameof(ClusterManifestProvider),
                ServiceLifecycleStage.RuntimeGrainServices,
                StartAsync,
                StopAsync);

            static Task NoOpStop(CancellationToken _) => Task.CompletedTask;
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
            Message = "Error retrieving silo manifest by hash for silo {SiloAddress}. Falling back to direct manifest fetch."
        )]
        private partial void LogDebugErrorRetrievingSiloManifestByHash(Exception exception, SiloAddress siloAddress);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Error retrieving cluster manifest from peer {SiloAddress}. Falling back to direct manifest fetch."
        )]
        private partial void LogDebugErrorRetrievingClusterManifestFromPeer(Exception exception, SiloAddress siloAddress);

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
