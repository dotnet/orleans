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
            _current = CreateClusterManifest(
                MajorMinorVersion.MinValue,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty);
            _updates = new AsyncEnumerable<ClusterManifest>(
                initialValue: _current,
                updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                onPublished: update => Interlocked.Exchange(ref _current, update));
        }

        public ClusterManifest Current => EnsureCurrentManifestVersion(_clusterMembershipService.CurrentSnapshot);

        public IAsyncEnumerable<ClusterManifest> Updates => _updates;

        public GrainManifest LocalGrainManifest { get; }

        private ClusterManifest EnsureCurrentManifestVersion(ClusterMembershipSnapshot clusterMembership)
        {
            var current = _current;
            var membershipVersion = clusterMembership.Version.Value;
            if (current.Version.Major > membershipVersion)
            {
                return current;
            }

            var synchronizedSilos = ApplySynchronousMembershipChanges(current.Silos, clusterMembership, out var modified);
            if (current.Version.Major == membershipVersion && !modified)
            {
                return current;
            }

            lock (_currentLock)
            {
                current = _current;
                if (current.Version.Major > membershipVersion)
                {
                    return current;
                }

                synchronizedSilos = ApplySynchronousMembershipChanges(current.Silos, clusterMembership, out modified);
                if (current.Version.Major == membershipVersion && !modified)
                {
                    return current;
                }

                var version = current.Version.Major == membershipVersion
                    ? new MajorMinorVersion(membershipVersion, current.Version.Minor + 1)
                    : new MajorMinorVersion(membershipVersion, 0);
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
            var existingManifest = EnsureCurrentManifestVersion(clusterMembership);
            if (existingManifest.Version.Major > clusterMembership.Version.Value)
            {
                return true;
            }

            var builder = existingManifest.Silos.ToBuilder();
            var modified = false;

            // Fill missing entries.
            var tasks = new List<Task<(SiloAddress Key, GrainManifest? Value, Exception? Exception)>>();
            foreach (var entry in clusterMembership.Members)
            {
                var member = entry.Value;
                if (member.Status != SiloStatus.Active)
                {
                    // If the member is not yet active, it may not be ready to process requests.
                    continue;
                }

                if (builder.ContainsKey(member.SiloAddress))
                {
                    // Manifest has already been retrieved for the cluster member.
                    continue;
                }

                if (member.SiloAddress.Equals(_localSiloAddress))
                {
                    // Local membership changes are applied synchronously by EnsureCurrentManifestVersion.
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
                    if (result.Value is not null)
                    {
                        builder[result.Key] = result.Value;
                    }
                }
            }

            // Regardless of success or failure, update the manifest if it has been modified.
            var version = new MajorMinorVersion(clusterMembership.Version.Value, existingManifest.Version.Minor + 1);
            if (modified)
            {
                var manifest = CreateClusterManifest(version, builder.ToImmutable());
                var publishSuccess = TryPublishManifest(manifest);
                return publishSuccess && fetchSuccess && ContainsAllActiveSilos(manifest.Silos, clusterMembership);
            }

            return fetchSuccess && ContainsAllActiveSilos(existingManifest.Silos, clusterMembership);
        }

        private ClusterManifest CreateClusterManifest(
            MajorMinorVersion version,
            ImmutableDictionary<SiloAddress, GrainManifest> silos)
        {
            var builder = ImmutableArray.CreateBuilder<GrainManifest>(silos.Count + 1);
            var containsLocalManifest = false;
            foreach (var manifest in silos.Values)
            {
                builder.Add(manifest);
                containsLocalManifest |= ReferenceEquals(manifest, LocalGrainManifest);
            }

            if (!containsLocalManifest)
            {
                builder.Add(LocalGrainManifest);
            }

            return new ClusterManifest(version, silos, builder.ToImmutable());
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

        private ImmutableDictionary<SiloAddress, GrainManifest> ApplySynchronousMembershipChanges(
            ImmutableDictionary<SiloAddress, GrainManifest> silos,
            ClusterMembershipSnapshot clusterMembership,
            out bool modified)
        {
            silos = RemoveNonActiveSilos(silos, clusterMembership, out modified);
            if (clusterMembership.GetSiloStatus(_localSiloAddress) != SiloStatus.Active || silos.ContainsKey(_localSiloAddress))
            {
                return silos;
            }

            modified = true;
            return silos.Add(_localSiloAddress, LocalGrainManifest);
        }

        private static ImmutableDictionary<SiloAddress, GrainManifest> RemoveNonActiveSilos(
            ImmutableDictionary<SiloAddress, GrainManifest> silos,
            ClusterMembershipSnapshot clusterMembership,
            out bool modified)
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

            modified = builder is not null;
            return builder?.ToImmutable() ?? silos;
        }

        private static bool ContainsAllActiveSilos(
            ImmutableDictionary<SiloAddress, GrainManifest> silos,
            ClusterMembershipSnapshot clusterMembership)
        {
            foreach (var entry in clusterMembership.Members)
            {
                var member = entry.Value;
                if (member.Status == SiloStatus.Active && !silos.ContainsKey(member.SiloAddress))
                {
                    return false;
                }
            }

            return true;
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
