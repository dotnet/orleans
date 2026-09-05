using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
        private const int MaxConcurrentPeerManifestProbes = 3;
        private static readonly TimeSpan PeerManifestProbeTimeout = TimeSpan.FromSeconds(1);
        private readonly SiloAddress _localSiloAddress;
        private readonly ILogger<ClusterManifestProvider> _logger;
        private readonly IServiceProvider _services;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly IFatalErrorHandler _fatalErrorHandler;
        private readonly TimeProvider _timeProvider;
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
        private int _peerProbeRound;
        private ConcurrentDictionary<ManifestHash, GrainManifest> _manifestCache = new();

        public ClusterManifestProvider(
            ILocalSiloDetails localSiloDetails,
            SiloManifestProvider siloManifestProvider,
            IClusterMembershipService clusterMembershipService,
            IFatalErrorHandler fatalErrorHandler,
            ILogger<ClusterManifestProvider> logger,
            IServiceProvider services,
            TimeProvider timeProvider)
        {
            _localSiloAddress = localSiloDetails.SiloAddress;
            _logger = logger;
            _services = services;
            _clusterMembershipService = clusterMembershipService;
            _fatalErrorHandler = fatalErrorHandler;
            _timeProvider = timeProvider;
            LocalGrainManifest = siloManifestProvider.SiloManifest;
            _current = CreateClusterManifest(
                MajorMinorVersion.MinValue,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty);
            _updates = new AsyncEnumerable<ClusterManifest>(
                initialValue: _current,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: OnManifestPublished);
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
            // An update overtaken by a newer publication keeps populating its original cache.
            var cache = Volatile.Read(ref _manifestCache);
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

            var peerRepairTask = missingSilos.Count > 1
                ? TryFillMissingManifestsFromPeers(clusterMembership, builder, missingSilos, cache)
                : Task.FromResult(false);

            var tasks = new Dictionary<SiloAddress, Task<(SiloAddress Key, GrainManifest? Value, Exception? Exception)>>();
            foreach (var siloAddress in missingSilos)
            {
                tasks.Add(siloAddress, GetManifest(siloAddress));
            }

            if (await peerRepairTask)
            {
                modified = true;
                var repairedSilos = builder.ToImmutable();
                var repairedManifest = CreateClusterManifest(
                    new MajorMinorVersion(clusterMembership.Version.Value, existingManifest.Version.Minor + 1),
                    repairedSilos);
                if (!TryPublishManifest(repairedManifest))
                {
                    return false;
                }

                existingManifest = repairedManifest;
                modified = false;
                foreach (var siloAddress in missingSilos)
                {
                    if (builder.ContainsKey(siloAddress))
                    {
                        tasks.Remove(siloAddress);
                    }
                }

                // Each fetch observes its own failures, so redundant requests can finish independently.
                if (tasks.Count == 0)
                {
                    return true;
                }
            }

            async Task<(SiloAddress Key, GrainManifest? Value, Exception? Exception)> GetManifest(SiloAddress siloAddress)
            {
                try
                {
                    var manifest = await GetSiloManifest(siloAddress, cache);
                    return (siloAddress, manifest, null);
                }
                catch (Exception exception)
                {
                    return (siloAddress, null, exception);
                }
            }

            var fetchSuccess = true;
            await Task.WhenAll(tasks.Values);
            foreach (var task in tasks.Values)
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
                var silos = builder.ToImmutable();
                var manifest = CreateClusterManifest(version, silos);
                var publishSuccess = TryPublishManifest(manifest);
                return publishSuccess && fetchSuccess;
            }
            return fetchSuccess;
        }

        private void OnManifestPublished(ClusterManifest manifest)
        {
            // Each publication seeds a new read-through cache with its live manifests. Fetches populate
            // their captured cache instance, keeping late completions isolated in their original epoch.
            var cache = new ConcurrentDictionary<ManifestHash, GrainManifest>();
            foreach (var grainManifest in manifest.AllGrainManifests)
            {
                cache[ManifestHashCalculator.ComputeHash(grainManifest)] = grainManifest;
            }

            cache[ManifestHashCalculator.ComputeHash(LocalGrainManifest)] = LocalGrainManifest;
            // Readers capture the cache before checking the manifest version, so expose the version first.
            Interlocked.Exchange(ref _current, manifest);
            Interlocked.Exchange(ref _manifestCache, cache);
        }

        private async Task<bool> TryFillMissingManifestsFromPeers(
            ClusterMembershipSnapshot clusterMembership,
            ImmutableDictionary<SiloAddress, GrainManifest>.Builder builder,
            List<SiloAddress> missingSilos,
            ConcurrentDictionary<ManifestHash, GrainManifest> cache)
        {
            var missing = new HashSet<SiloAddress>(missingSilos);
            var modified = false;
            var peers = clusterMembership.Members.Values
                .Where(static member => member.Status == SiloStatus.Active)
                .Select(static member => member.SiloAddress)
                .Where(peer => !peer.Equals(_localSiloAddress))
                .OrderBy(static silo => silo)
                .ToArray();
            if (peers.Length == 0)
            {
                return false;
            }

            var round = Interlocked.Increment(ref _peerProbeRound);
            var start = (int)((uint)(_localSiloAddress.GetConsistentHashCode() + round) % (uint)peers.Length);
            var probeCount = Math.Min(MaxConcurrentPeerManifestProbes, peers.Length);
            var probes = new Task<PeerManifestProbeResult?>[probeCount];
            for (var i = 0; i < probeCount; i++)
            {
                probes[i] = ProbePeerForManifests(peers[(start + i) % peers.Length], missingSilos, cache);
            }

            var results = await Task.WhenAll(probes);
            foreach (var result in results)
            {
                if (result is null || missing.Count == 0)
                {
                    continue;
                }

                FillFromCachedHashes(result.Summary, missing, builder, cache, ref modified);
                if (result.Update?.SiloManifests is { } manifests)
                {
                    foreach (var silo in missing.ToArray())
                    {
                        if (!result.Summary.SiloManifestHashes.TryGetValue(silo, out var expectedHash)
                            || !manifests.TryGetValue(silo, out var manifest)
                            || ManifestHashCalculator.ComputeHash(manifest) != expectedHash)
                        {
                            continue;
                        }

                        cache[expectedHash] = manifest;
                        builder[silo] = manifest;
                        missing.Remove(silo);
                        modified = true;
                    }
                }
            }

            return modified;
        }

        private async Task<PeerManifestProbeResult?> ProbePeerForManifests(
            SiloAddress peer,
            IReadOnlyCollection<SiloAddress> missingSilos,
            ConcurrentDictionary<ManifestHash, GrainManifest> cache)
        {
            var startedAt = _timeProvider.GetTimestamp();
            Task? probeTask = null;
            try
            {
                var remoteManifestProvider = _grainFactory!.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, peer);
                var summaryTask = remoteManifestProvider.GetClusterManifestHashSummary().AsTask();
                probeTask = summaryTask;
                var summary = await summaryTask
                    .WaitAsync(PeerManifestProbeTimeout, _timeProvider, _shutdownCts.Token);
                probeTask = null;
                if (missingSilos.All(silo =>
                    summary.SiloManifestHashes.TryGetValue(silo, out var hash)
                    && cache.ContainsKey(hash)))
                {
                    return new(summary, Update: null);
                }

                var remaining = PeerManifestProbeTimeout - _timeProvider.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }

                // No per-peer manifest body is retained, so request a complete update instead of synthesizing a
                // baseline from the local provider's version.
                var updateTask = remoteManifestProvider.GetClusterManifestUpdate(MajorMinorVersion.MinValue).AsTask();
                probeTask = updateTask;
                var update = await updateTask
                    .WaitAsync(remaining, _timeProvider, _shutdownCts.Token);
                probeTask = null;
                return new(summary, update);
            }
            catch (TimeoutException)
            {
                ObserveLatePeerProbeFailure(probeTask, peer);
                LogDebugClusterManifestPeerProbeTimedOut(peer, PeerManifestProbeTimeout);
                return null;
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                ObserveLatePeerProbeFailure(probeTask, peer);
                throw;
            }
            catch (Exception exception)
            {
                LogDebugErrorRetrievingClusterManifestFromPeer(exception, peer);
                return null;
            }
        }

        private void ObserveLatePeerProbeFailure(Task? probeTask, SiloAddress peer)
        {
            if (probeTask is not null)
            {
                ObserveLatePeerProbeFailureAsync(probeTask, peer).Ignore();
            }
        }

        private async Task ObserveLatePeerProbeFailureAsync(Task probeTask, SiloAddress peer)
        {
            try
            {
                await probeTask;
            }
            catch (Exception exception)
            {
                LogDebugLateClusterManifestPeerProbeFailure(exception, peer);
            }
        }

        private sealed record PeerManifestProbeResult(
            ClusterManifestHashSummary Summary,
            ClusterManifestUpdate? Update);

        private static void FillFromCachedHashes(
            ClusterManifestHashSummary summary,
            HashSet<SiloAddress> missing,
            ImmutableDictionary<SiloAddress, GrainManifest>.Builder builder,
            ConcurrentDictionary<ManifestHash, GrainManifest> cache,
            ref bool modified)
        {
            foreach (var silo in missing.ToArray())
            {
                if (summary.SiloManifestHashes.TryGetValue(silo, out var hash)
                    && cache.TryGetValue(hash, out var cached))
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

        private async Task<GrainManifest> GetSiloManifest(
            SiloAddress siloAddress,
            ConcurrentDictionary<ManifestHash, GrainManifest> cache)
        {
            try
            {
                var remoteManifestProvider = _grainFactory!.GetSystemTarget<IClusterManifestSystemTarget>(Constants.ManifestProviderType, siloAddress);
                var hash = await AwaitManifestRequest(remoteManifestProvider.GetSiloManifestHash().AsTask());
                if (cache.TryGetValue(hash, out var cached))
                {
                    return cached;
                }

                var manifest = await AwaitManifestRequest(remoteManifestProvider.GetSiloManifestByHash(hash).AsTask());
                if (manifest is not null && ManifestHashCalculator.ComputeHash(manifest) == hash)
                {
                    cache[hash] = manifest;
                    return manifest;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_shutdownCts.IsCancellationRequested)
            {
                LogDebugErrorRetrievingSiloManifestByHash(exception, siloAddress);
            }
            var legacyManifestProvider = _grainFactory!.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, siloAddress);
            var legacyManifest = await AwaitManifestRequest(legacyManifestProvider.GetSiloManifest(_shutdownCts.Token).AsTask());
            cache[ManifestHashCalculator.ComputeHash(legacyManifest)] = legacyManifest;
            return legacyManifest;
        }

        private async Task<T> AwaitManifestRequest<T>(Task<T> request)
        {
            try
            {
                return await request.WaitAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
                request.Ignore();
                throw;
            }
        }

        [MemberNotNull(nameof(_runTask))]
        private Task StartAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_grainFactory is not null);
            _runTask = Task.Run(ProcessMembershipUpdates, CancellationToken.None);
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
            Message = "Cluster manifest peer probe to {SiloAddress} exceeded {Timeout}. Direct manifest fetch continues."
        )]
        private partial void LogDebugClusterManifestPeerProbeTimedOut(SiloAddress siloAddress, TimeSpan timeout);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Cluster manifest peer probe task for {SiloAddress} faulted after the caller stopped waiting."
        )]
        private partial void LogDebugLateClusterManifestPeerProbeFailure(Exception exception, SiloAddress siloAddress);

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
