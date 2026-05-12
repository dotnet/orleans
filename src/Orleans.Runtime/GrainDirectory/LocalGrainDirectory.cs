using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory
{
    internal sealed partial class LocalGrainDirectory : ILocalGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger log;
        private readonly SiloAddress? seed;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IClusterMembershipService clusterMembershipService;
        private readonly IInternalGrainFactory grainFactory;
        private readonly ActivationDirectory localActivations;
        private readonly InsideRuntimeClient runtimeClient;
        private readonly IServiceProvider _serviceProvider;
        private readonly CancellationTokenSource _membershipUpdatesCancellation = new();
        private DirectoryMembership directoryMembership = DirectoryMembership.Default;
        private ClusterMembershipSnapshot appliedClusterMembershipSnapshot = ClusterMembershipSnapshot.Default;
        private GrainDirectoryResolver? grainDirectoryResolver;
        private bool hasAppliedClusterMembershipSnapshot;

        // Consider: move these constants into an appropriate place
        internal const int HOP_LIMIT = 6; // forward a remote request no more than 5 times
        public static readonly TimeSpan RETRY_DELAY = TimeSpan.FromMilliseconds(200); // Pause 200ms between forwards to let the membership directory settle down
        internal bool Running;
        private Task? membershipUpdatesTask;

        internal SiloAddress MyAddress { get; }

        internal IGrainDirectoryCache DirectoryCache { get; }
        private readonly bool disposeDirectoryCache;
        internal LocalGrainDirectoryPartition DirectoryPartition { get; }

        public RemoteGrainDirectory RemoteGrainDirectory { get; }
        public RemoteGrainDirectory CacheValidator { get; }
        internal LocalGrainDirectoryClientCompatibility? DistributedGrainDirectoryClientCompatibility { get; }
        internal ImmutableArray<LocalGrainDirectoryPartitionCompatibility> DistributedGrainDirectoryPartitionCompatibilities { get; }

        internal GrainDirectoryHandoffManager HandoffManager { get; }

        public LocalGrainDirectory(
            IServiceProvider serviceProvider,
            ILocalSiloDetails siloDetails,
            ISiloStatusOracle siloStatusOracle,
            IClusterMembershipService clusterMembershipService,
            IInternalGrainFactory grainFactory,
            Factory<LocalGrainDirectoryPartition> grainDirectoryPartitionFactory,
            IOptions<DevelopmentClusterMembershipOptions> developmentClusterMembershipOptions,
            IOptions<GrainDirectoryOptions> grainDirectoryOptions,
            ILoggerFactory loggerFactory,
            SystemTargetShared systemTargetShared)
        {
            this.log = loggerFactory.CreateLogger<LocalGrainDirectory>();

            MyAddress = siloDetails.SiloAddress;

            this.siloStatusOracle = siloStatusOracle;
            this.clusterMembershipService = clusterMembershipService;
            this.grainFactory = grainFactory;
            this.localActivations = systemTargetShared.ActivationDirectory;
            this.runtimeClient = systemTargetShared.RuntimeClient;

            DirectoryCache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(serviceProvider, grainDirectoryOptions.Value, out this.disposeDirectoryCache);

            var primarySiloEndPoint = developmentClusterMembershipOptions.Value.PrimarySiloEndpoint;
            if (primarySiloEndPoint != null)
            {
                this.seed = this.MyAddress.Endpoint.Equals(primarySiloEndPoint) ? this.MyAddress : SiloAddress.New(primarySiloEndPoint, 0);
            }

            DirectoryPartition = grainDirectoryPartitionFactory();
            HandoffManager = new GrainDirectoryHandoffManager(this, siloStatusOracle, grainFactory, grainDirectoryPartitionFactory, loggerFactory);

            // When DistributedGrainDirectory is active, it registers its own IRemoteGrainDirectory system targets.
            // In that case, create the RemoteGrainDirectory objects (still needed for WorkItemGroup scheduling)
            // but skip registering them as system targets to avoid conflicts.
            var distributedDirectoryActive = serviceProvider.GetService<DistributedGrainDirectory>() is not null;
            RemoteGrainDirectory = new RemoteGrainDirectory(this, Constants.DirectoryServiceType, systemTargetShared, registerAsSystemTarget: !distributedDirectoryActive);
            CacheValidator = new RemoteGrainDirectory(this, Constants.DirectoryCacheValidatorType, systemTargetShared, registerAsSystemTarget: !distributedDirectoryActive);
            DistributedGrainDirectoryClientCompatibility = distributedDirectoryActive ? null : new LocalGrainDirectoryClientCompatibility(systemTargetShared);
            if (!distributedDirectoryActive)
            {
                var partitionsPerSilo = grainDirectoryOptions.Value.PartitionsPerSilo;
                ArgumentOutOfRangeException.ThrowIfLessThan(partitionsPerSilo, 1, nameof(GrainDirectoryOptions.PartitionsPerSilo));
                var compatibilityPartitions = ImmutableArray.CreateBuilder<LocalGrainDirectoryPartitionCompatibility>(partitionsPerSilo);
                for (var partitionIndex = 0; partitionIndex < partitionsPerSilo; partitionIndex++)
                {
                    compatibilityPartitions.Add(new LocalGrainDirectoryPartitionCompatibility(this, systemTargetShared, partitionIndex));
                }

                DistributedGrainDirectoryPartitionCompatibilities = compatibilityPartitions.MoveToImmutable();
            }

            this.directoryMembership = DirectoryMembership.Create([MyAddress]);

            DirectoryInstruments.RegisterDirectoryPartitionSizeObserve(() => DirectoryPartition.Count);
            DirectoryInstruments.RegisterMyPortionRingDistanceObserve(() => RingDistanceToSuccessor());
            DirectoryInstruments.RegisterMyPortionRingPercentageObserve(() => this.RingDistanceToSuccessor() / (float)(int.MaxValue * 2L) * 100);
            DirectoryInstruments.RegisterMyPortionAverageRingPercentageObserve(() =>
            {
                var ring = this.directoryMembership.MembershipRingList;
                return ring.Count == 0 ? 0 : (100 / (float)ring.Count);
            });
            DirectoryInstruments.RegisterRingSizeObserve(() => this.directoryMembership.MembershipRingList.Count);
            _serviceProvider = serviceProvider;
        }

        public void Start()
        {
            LogDebugStart();

            Running = true;
            using var _ = new ExecutionContextSuppressor();
            membershipUpdatesTask = Task.Run(() => ProcessMembershipUpdates(_membershipUpdatesCancellation.Token));
        }

        // Note that this implementation stops processing directory change requests (Register, Unregister, etc.) when the Stop event is raised.
        // This means that there may be a short period during which no silo believes that it is the owner of directory information for a set of
        // grains (for update purposes), which could cause application requests that require a new activation to be created to time out.
        // The alternative would be to allow the silo to process requests after it has handed off its partition, in which case those changes
        // would receive successful responses but would not be reflected in the eventual state of the directory.
        // It's easy to change this, if we think the trade-off is better the other way.
        public async Task StopAsync()
        {
            // This will cause remote write requests to be forwarded to the silo that will become the new owner.
            // Requests might bounce back and forth for a while as membership stabilizes, but they will either be served by the
            // new owner of the grain, or will wind up failing. In either case, we avoid requests succeeding at this silo after we've
            // begun stopping, which could cause them to not get handed off to the new owner.

            //mark Running as false will exclude myself from CalculateGrainDirectoryPartition(grainId)
            Running = false;
            _membershipUpdatesCancellation.Cancel();
            try
            {
                if (membershipUpdatesTask is { } task)
                {
                    await task.SuppressThrowing();
                }
            }
            finally
            {
                _membershipUpdatesCancellation.Dispose();
            }

            if (this.disposeDirectoryCache)
            {
                try
                {
                    await GrainDirectoryCacheFactory.DisposeGrainDirectoryCacheAsync(DirectoryCache);
                }
                catch (Exception exception)
                {
                    LogWarningDisposeDirectoryCacheFailed(exception);
                }
            }

            DirectoryPartition.Clear();
            DirectoryCache.Clear();
        }

        private async Task ProcessMembershipUpdates(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ApplyMembershipSnapshot();

                    await foreach (var _ in clusterMembershipService.MembershipUpdates.WithCancellation(cancellationToken))
                    {
                        // Always apply the latest snapshot.
                        await ApplyMembershipSnapshot();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogErrorProcessingMembershipUpdates(exception);

                    try
                    {
                        await clusterMembershipService.Refresh(cancellationToken: cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception refreshException)
                    {
                        LogWarningRefreshingClusterMembershipFailed(refreshException);
                    }

                    await Task.Delay(RETRY_DELAY, cancellationToken).SuppressThrowing();
                }
            }
        }

        private Task ApplyMembershipSnapshot()
        {
            return CacheValidator.RunOrQueueTask(() =>
            {
                ApplyMembershipSnapshotCore();
                return Task.CompletedTask;
            });

            void ApplyMembershipSnapshotCore()
            {
                if (!Running)
                {
                    return;
                }

                var snapshot = clusterMembershipService.CurrentSnapshot;
                var previousSnapshot = hasAppliedClusterMembershipSnapshot ? appliedClusterMembershipSnapshot : ClusterMembershipSnapshot.Default;
                if (hasAppliedClusterMembershipSnapshot && snapshot.Version <= previousSnapshot.Version)
                {
                    return;
                }

                var previousMembership = CreateDirectoryMembership(previousSnapshot);
                var targetMembership = CreateDirectoryMembership(snapshot);
                this.directoryMembership = targetMembership;

                var removedSilos = GetMembershipDifference(previousMembership, targetMembership);
                var addedSilos = GetMembershipDifference(targetMembership, previousMembership);

                ProcessSiloStatusChanges(snapshot, previousSnapshot, previousMembership);
                AdjustLocalDirectory(snapshot);
                AdjustLocalCache(snapshot, targetMembership);

                foreach (var silo in removedSilos)
                {
                    LogDebugSiloRemovedSilo(MyAddress, silo);
                }

                foreach (var silo in addedSilos)
                {
                    HandoffManager.ProcessSiloAddEvent(silo);
                    LogDebugSiloAddedSilo(MyAddress, silo);
                }

                appliedClusterMembershipSnapshot = snapshot;
                hasAppliedClusterMembershipSnapshot = true;
            }
        }

        private void ProcessSiloStatusChanges(
            ClusterMembershipSnapshot snapshot,
            ClusterMembershipSnapshot previousSnapshot,
            DirectoryMembership previousMembership)
        {
            var changes = previousSnapshot.Version != MembershipVersion.MinValue
                ? snapshot.CreateUpdate(previousSnapshot)
                : snapshot.AsUpdate();
            var statusChanges = new List<ClusterMember>();
            foreach (var change in changes.Changes)
            {
                if (!change.SiloAddress.Equals(MyAddress) && change.Status.IsTerminating())
                {
                    statusChanges.Add(change);
                }
            }

            statusChanges.Sort(static (left, right) => CompareSiloAddress(left.SiloAddress, right.SiloAddress));
            foreach (var change in statusChanges)
            {
                OnSiloStatusChange(previousMembership, change.SiloAddress, change.Status);
            }
        }

        private DirectoryMembership CreateDirectoryMembership(ClusterMembershipSnapshot snapshot)
        {
            var members = new List<SiloAddress>();
            foreach (var member in snapshot.Members)
            {
                if (member.Value.Status == SiloStatus.Active)
                {
                    members.Add(member.Key);
                }
            }

            if (Running && !members.Contains(MyAddress))
            {
                members.Add(MyAddress);
            }

            return DirectoryMembership.Create(members);
        }

        private List<SiloAddress> GetMembershipDifference(
            DirectoryMembership currentMembership,
            DirectoryMembership otherMembership)
        {
            var result = new List<SiloAddress>();
            foreach (var silo in currentMembership.MembershipRingList)
            {
                if (!silo.Equals(MyAddress) && !otherMembership.MembershipCache.Contains(silo))
                {
                    result.Add(silo);
                }
            }

            return result;
        }

        private Task RefreshMembershipIfNewer(GrainAddress address, GrainAddress? previousAddress = null)
        {
            var targetVersion = address.MembershipVersion;
            if (previousAddress is not null && previousAddress.MembershipVersion > targetVersion)
            {
                targetVersion = previousAddress.MembershipVersion;
            }

            return RefreshMembershipIfNewer(targetVersion);
        }

        private Task RefreshMembershipIfNewer(List<GrainAddress> addresses)
        {
            var targetVersion = MembershipVersion.MinValue;
            foreach (var address in addresses)
            {
                if (address.MembershipVersion > targetVersion)
                {
                    targetVersion = address.MembershipVersion;
                }
            }

            return RefreshMembershipIfNewer(targetVersion);
        }

        private async Task RefreshMembershipIfNewer(MembershipVersion targetVersion)
        {
            if (targetVersion <= appliedClusterMembershipSnapshot.Version)
            {
                return;
            }

            if (targetVersion > clusterMembershipService.CurrentSnapshot.Version)
            {
                await clusterMembershipService.Refresh(targetVersion);
            }

            await ApplyMembershipSnapshot();
        }

        private void OnSiloStatusChange(DirectoryMembership previousMembership, SiloAddress updatedSilo, SiloStatus status)
        {
            if (updatedSilo.Equals(MyAddress) || !status.IsTerminating())
            {
                return;
            }

            if (status == SiloStatus.Dead)
            {
                runtimeClient.BreakOutstandingMessagesToSilo(updatedSilo);
            }

            var activationsToShutdown = new List<IGrainContext>();
            var resolver = grainDirectoryResolver ??= _serviceProvider.GetRequiredService<GrainDirectoryResolver>();
            foreach (var activation in localActivations)
            {
                try
                {
                    var activationData = activation.Value;
                    var placementStrategy = activationData.GetComponent<PlacementStrategy>();
                    var isUsingGrainDirectory = placementStrategy is { IsUsingGrainDirectory: true };
                    if (!isUsingGrainDirectory || !resolver.IsUsingDefaultDirectory(activationData.GrainId.Type))
                    {
                        continue;
                    }

                    if (!updatedSilo.Equals(CalculateGrainDirectoryPartition(activationData.GrainId, previousMembership)))
                    {
                        continue;
                    }

                    activationsToShutdown.Add(activationData);
                }
                catch (Exception exception)
                {
                    LogErrorSiloStatusChangeNotification(new(updatedSilo), exception);
                }
            }

            if (activationsToShutdown.Count == 0)
            {
                return;
            }

            LogInfoSiloStatusChangeNotification(activationsToShutdown.Count, new(updatedSilo), status);

            var reasonText = $"This activation is being deactivated because server {updatedSilo} entered status {status} and was responsible for this activation's grain directory registration.";
            var reason = new DeactivationReason(DeactivationReasonCode.DirectoryFailure, reasonText);
            foreach (var activation in activationsToShutdown)
            {
                try
                {
                    activation.Deactivate(reason, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    LogErrorDeactivatingActivationForRemovedSilo(exception, activation.GrainId, new(updatedSilo));
                }
            }
        }

        private void AdjustLocalDirectory(ClusterMembershipSnapshot snapshot)
        {
            var activationsToRemove = new List<(GrainId, ActivationId)>();
            foreach (var entry in this.DirectoryPartition.GetItems())
            {
                if (entry.Value.Activation is { } address)
                {
                    if (IsDefunctActivation(address, snapshot))
                    {
                        activationsToRemove.Add((entry.Key, address.ActivationId));
                    }
                }
            }

            foreach (var activation in activationsToRemove)
            {
                DirectoryPartition.RemoveActivation(activation.Item1, activation.Item2);
            }
        }

        private void AdjustLocalCache(ClusterMembershipSnapshot snapshot, DirectoryMembership targetMembership)
        {
            foreach (var tuple in DirectoryCache.KeyValues)
            {
                var activationAddress = tuple.ActivationAddress;

                // Remove entries now owned by me. They should be retrieved from my directory partition.
                if (MyAddress.Equals(CalculateGrainDirectoryPartition(activationAddress.GrainId, targetMembership)))
                {
                    DirectoryCache.Remove(activationAddress.GrainId);
                    continue;
                }

                if (IsDefunctActivation(activationAddress, snapshot))
                {
                    DirectoryCache.Remove(activationAddress.GrainId);
                }
            }
        }

        private static bool IsDefunctActivation(GrainAddress address, ClusterMembershipSnapshot snapshot)
        {
            if (address.SiloAddress is not { } silo)
            {
                return true;
            }

            if (snapshot.Members.TryGetValue(silo, out var member))
            {
                // If this is a known host, remove the activation if the host is dead.
                return member.Status == SiloStatus.Dead;
            }

            // If this is not a known host, remove the activation if it was registered at an older membership version.
            // This indicates that the host must have been removed.
            // Hosts cannot activate grains before they are active, and we ensure that we refresh the membership before processing messages,
            // so this is a reliable indicator of a defunct activation.
            return address.MembershipVersion < snapshot.Version;
        }

        internal SiloAddress? FindPredecessor(SiloAddress silo)
        {
            var existing = directoryMembership.MembershipRingList;
            int index = existing.IndexOf(silo);
            if (index == -1)
            {
                LogWarningFindPredecessorSiloNotInList(silo);
                return null;
            }

            return existing.Count > 1 ? existing[(index == 0 ? existing.Count : index) - 1] : null;
        }

        internal SiloAddress? FindSuccessor(SiloAddress silo)
        {
            var existing = directoryMembership.MembershipRingList;
            int index = existing.IndexOf(silo);
            if (index == -1)
            {
                LogWarningFindSuccessorSiloNotInList(silo);
                return null;
            }

            return existing.Count > 1 ? existing[(index + 1) % existing.Count] : null;
        }

        private bool IsValidSilo(SiloAddress? silo) => siloStatusOracle.IsFunctionalDirectory(silo);

        /// <summary>
        /// Finds the silo that owns the directory information for the given grain ID.
        /// This method will only be null when I'm the only silo in the cluster and I'm shutting down
        /// </summary>
        /// <param name="grainId"></param>
        /// <returns></returns>
        public SiloAddress? CalculateGrainDirectoryPartition(GrainId grainId)
            => CalculateGrainDirectoryPartition(grainId, this.directoryMembership);

        private SiloAddress? CalculateGrainDirectoryPartition(GrainId grainId, DirectoryMembership membership)
        {
            // give a special treatment for special grains
            if (grainId.IsSystemTarget())
            {
                if (Constants.SystemMembershipTableType.Equals(grainId.Type))
                {
                    if (seed == null)
                    {
                        var errorMsg =
                            $"Development clustering cannot run without a primary silo. " +
                            $"Please configure {nameof(DevelopmentClusterMembershipOptions)}.{nameof(DevelopmentClusterMembershipOptions.PrimarySiloEndpoint)} " +
                            "or provide a primary silo address to the UseDevelopmentClustering extension. " +
                            "Alternatively, you may want to use reliable membership, such as Azure Table.";
                        throw new ArgumentException(errorMsg, "grainId = " + grainId);
                    }
                }

                LogTraceSystemTargetLookup(MyAddress, grainId, MyAddress);

                // every silo owns its system targets
                return MyAddress;
            }

            SiloAddress? siloAddress = null;
            int hash = unchecked((int)grainId.GetUniformHashCode());

            // excludeMySelf from being a TargetSilo if we're not running and the excludeThisSIloIfStopping flag is true. see the comment in the Stop method.
            // excludeThisSIloIfStopping flag was removed because we believe that flag complicates things unnecessarily. We can add it back if it turns out that flag
            // is doing something valuable.
            bool excludeMySelf = !Running;

            var existing = membership;
            if (existing.MembershipRingList.Count == 0)
            {
                // If the membership ring is empty, then we're the owner by default unless we're stopping.
                return !Running ? null : MyAddress;
            }

            // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes
            for (var index = existing.MembershipRingList.Count - 1; index >= 0; --index)
            {
                var item = existing.MembershipRingList[index];
                if (IsSiloNextInTheRing(item, hash, excludeMySelf))
                {
                    siloAddress = item;
                    break;
                }
            }

            if (siloAddress == null)
            {
                // If not found in the traversal, last silo will do (we are on a ring).
                // We checked above to make sure that the list isn't empty, so this should always be safe.
                siloAddress = existing.MembershipRingList[existing.MembershipRingList.Count - 1];
                // Make sure it's not us...
                if (siloAddress.Equals(MyAddress) && excludeMySelf)
                {
                    siloAddress = existing.MembershipRingList.Count > 1 ? existing.MembershipRingList[existing.MembershipRingList.Count - 2] : null;
                }
            }

            if (log.IsEnabled(LogLevel.Trace))
                LogTraceCalculatedDirectoryPartitionOwner(
                    MyAddress,
                    siloAddress,
                    grainId,
                    hash,
                    siloAddress?.GetConsistentHashCode());
            return siloAddress;
        }

        public SiloAddress? CheckIfShouldForward(GrainId grainId, int hopCount, string operationDescription)
        {
            var owner = CalculateGrainDirectoryPartition(grainId);

            if (owner is null || owner.Equals(MyAddress))
            {
                // Either we don't know about any other silos and we're stopping, or we are the owner.
                // Null indicates that the operation should be performed locally.
                // In the case that this host is terminating, any grain registered to this host must terminate.
                return null;
            }

            if (hopCount >= HOP_LIMIT)
            {
                // we are not forwarding because there were too many hops already
                throw new OrleansException($"Silo {MyAddress} is not owner of {grainId}, cannot forward {operationDescription} to owner {owner} because hop limit is reached");
            }

            // forward to the silo that we think is the owner
            return owner;
        }

        public Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount) => RegisterAsync(address, previousAddress: null, hopCount: hopCount);

        public async Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.RegistrationsSingleActRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.RegistrationsSingleActIssued.Add(1);
            }

            await RefreshMembershipIfNewer(address, previousAddress);

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "RegisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "RegisterAsync");
                if (forwardAddress is not null)
                {
                    int hash = unchecked((int)address.GrainId.GetUniformHashCode());
                    LogWarningRegisterAsyncNotOwner(
                        address,
                        hash,
                        forwardAddress,
                        hopCount);
                }
            }

            if (forwardAddress == null)
            {
                DirectoryInstruments.RegistrationsSingleActLocal.Add(1);

                var result = DirectoryPartition.AddSingleActivation(address, previousAddress);

                // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                DirectoryCache.AddOrUpdate(result.Address, result.VersionTag);
                return result;
            }
            else
            {
                DirectoryInstruments.RegistrationsSingleActRemoteSent.Add(1);

                // otherwise, notify the owner
                AddressAndTag result = await GetDirectoryReference(forwardAddress).RegisterAsync(address, previousAddress, hopCount + 1);

                // Caching optimization:
                // cache the result of a successful RegisterSingleActivation call, only if it is not a duplicate activation.
                // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                if (result.Address == null) return result;

                if (!address.Equals(result.Address) || !IsValidSilo(address.SiloAddress)) return result;

                // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                DirectoryCache.AddOrUpdate(result.Address, result.VersionTag);

                return result;
            }
        }

        public async Task UnregisterAfterNonexistingActivation(GrainAddress addr, SiloAddress origin)
        {
            LogTraceUnregisterAfterNonexistingActivation(addr, origin);

            await RefreshMembershipIfNewer(addr);

            if (origin == null || this.directoryMembership.MembershipCache.Contains(origin))
            {
                // the request originated in this cluster, call unregister here
                await UnregisterAsync(addr, UnregistrationCause.NonexistentActivation, 0);
            }
            else
            {
                // the request originated in another cluster, call unregister there
                var remoteDirectory = GetDirectoryReference(origin);
                await remoteDirectory.UnregisterAsync(addr, UnregistrationCause.NonexistentActivation);
            }
        }

        public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.UnregistrationsRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.UnregistrationsIssued.Add(1);
            }

            if (hopCount == 0)
                InvalidateCacheEntry(address);

            await RefreshMembershipIfNewer(address);

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "UnregisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "UnregisterAsync");
                LogWarningUnregisterAsyncNotOwner(
                    address,
                    forwardAddress,
                    hopCount);
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryInstruments.UnregistrationsLocal.Add(1);
                DirectoryPartition.RemoveActivation(address.GrainId, address.ActivationId, cause);
            }
            else
            {
                DirectoryInstruments.UnregistrationsRemoteSent.Add(1);
                // otherwise, notify the owner
                await GetDirectoryReference(forwardAddress).UnregisterAsync(address, cause, hopCount + 1);
            }
        }

        // helper method to avoid code duplication inside UnregisterManyAsync
        private void UnregisterOrPutInForwardList(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount,
            ref Dictionary<SiloAddress, List<GrainAddress>>? forward, string context)
        {
            foreach (var address in addresses)
            {
                // see if the owner is somewhere else (returns null if we are owner)
                var forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, context);

                if (forwardAddress != null)
                {
                    forward ??= new();
                    if (!forward.TryGetValue(forwardAddress, out var list))
                        forward[forwardAddress] = list = new();
                    list.Add(address);
                }
                else
                {
                    // we are the owner
                    DirectoryInstruments.UnregistrationsLocal.Add(1);

                    DirectoryPartition.RemoveActivation(address.GrainId, address.ActivationId, cause);
                }
            }
        }

        public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.UnregistrationsManyRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.UnregistrationsManyIssued.Add(1);
            }

            await RefreshMembershipIfNewer(addresses);

            Dictionary<SiloAddress, List<GrainAddress>>? forwardlist = null;

            UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist, "UnregisterManyAsync");

            // before forwarding to other silos, we insert a retry delay and re-check destination
            if (hopCount > 0 && forwardlist != null)
            {
                await Task.Delay(RETRY_DELAY);
                Dictionary<SiloAddress, List<GrainAddress>>? forwardlist2 = null;
                UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist2, "UnregisterManyAsync");
                forwardlist = forwardlist2;
                if (forwardlist != null)
                {
                    LogWarningUnregisterManyAsyncNotOwner(
                        forwardlist.Count,
                        hopCount);
                }
            }

            // forward the requests
            if (forwardlist != null)
            {
                var tasks = new List<Task>();
                foreach (var kvp in forwardlist)
                {
                    DirectoryInstruments.UnregistrationsManyRemoteSent.Add(1);
                    tasks.Add(GetDirectoryReference(kvp.Key).UnregisterManyAsync(kvp.Value, cause, hopCount + 1));
                }

                // wait for all the requests to finish
                await Task.WhenAll(tasks);
            }
        }


        public bool LocalLookup(GrainId grain, out AddressAndTag result)
        {
            DirectoryInstruments.LookupsLocalIssued.Add(1);

            var silo = CalculateGrainDirectoryPartition(grain);

            LogDebugLocalLookupAttempt(
                MyAddress,
                grain,
                silo,
                new(grain),
                new(silo));

            //this will only happen if I'm the only silo in the cluster and I'm shutting down
            if (silo == null)
            {
                LogTraceLocalLookupMineNull(grain);
                result = default;
                return false;
            }

            // handle cache
            DirectoryInstruments.LookupsCacheIssued.Add(1);
            var address = GetLocalCacheData(grain);
            if (address != default)
            {
                result = new(address, 0);

                LogTraceLocalLookupCache(grain, result.Address);
                DirectoryInstruments.LookupsCacheSuccesses.Add(1);
                DirectoryInstruments.LookupsLocalSuccesses.Add(1);
                return true;
            }

            // check if we own the grain
            if (silo.Equals(MyAddress))
            {
                DirectoryInstruments.LookupsLocalDirectoryIssued.Add(1);
                result = GetLocalDirectoryData(grain);
                if (result.Address == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were
                    // some recent changes in the membership
                    LogTraceLocalLookupMineNull(grain);
                    return false;
                }
                LogTraceLocalLookupMine(grain, result.Address);
                DirectoryInstruments.LookupsLocalDirectorySuccesses.Add(1);
                DirectoryInstruments.LookupsLocalSuccesses.Add(1);
                return true;
            }

            LogTraceTryFullLookupElse(grain);
            result = default;
            return false;
        }

        public AddressAndTag GetLocalDirectoryData(GrainId grain) => DirectoryPartition.LookUpActivation(grain);

        public GrainAddress? GetLocalCacheData(GrainId grain) => DirectoryCache.LookUp(grain, out var cache) && IsValidSilo(cache.SiloAddress) ? cache : null;

        public async Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.LookupsRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.LookupsFullIssued.Add(1);
            }

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "LookUpAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "LookUpAsync");
                if (forwardAddress is not null)
                {
                    int hash = unchecked((int)grainId.GetUniformHashCode());
                    LogWarningLookupAsyncNotOwner(
                        grainId,
                        hash,
                        forwardAddress,
                        hopCount);
                }
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryInstruments.LookupsLocalDirectoryIssued.Add(1);
                var localResult = DirectoryPartition.LookUpActivation(grainId);
                if (localResult.Address == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were
                    // some recent changes in the membership
                    LogTraceFullLookupMineNone(grainId);
                    return new(default, GrainInfo.NO_ETAG);
                }

                LogTraceFullLookupMine(grainId, localResult.Address);
                DirectoryInstruments.LookupsLocalDirectorySuccesses.Add(1);
                return localResult;
            }
            else
            {
                // Just a optimization. Why sending a message to someone we know is not valid.
                if (!IsValidSilo(forwardAddress))
                {
                    throw new OrleansException($"Current directory at {MyAddress} is not stable to perform the lookup for grainId {grainId} (it maps to {forwardAddress}, which is not a valid silo). Retry later.");
                }

                DirectoryInstruments.LookupsRemoteSent.Add(1);
                var result = await GetDirectoryReference(forwardAddress).LookupAsync(grainId, hopCount + 1);

                // update the cache
                if (result.Address is { } address && IsValidSilo(address.SiloAddress))
                {
                    DirectoryCache.AddOrUpdate(address, result.VersionTag);
                }

                LogTraceFullLookupRemote(grainId, result.Address);

                return result;
            }
        }

        public async Task DeleteGrainAsync(GrainId grainId, int hopCount)
        {
            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "DeleteGrainAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "DeleteGrainAsync");
                LogWarningDeleteGrainAsyncNotOwner(
                    grainId,
                    forwardAddress,
                    hopCount);
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryPartition.RemoveGrain(grainId);
            }
            else
            {
                // otherwise, notify the owner
                DirectoryCache.Remove(grainId);
                await GetDirectoryReference(forwardAddress).DeleteGrainAsync(grainId, hopCount + 1);
            }
        }

        public void InvalidateCacheEntry(GrainId grainId)
        {
            DirectoryCache.Remove(grainId);
        }

        public void InvalidateCacheEntry(GrainAddress activationAddress)
        {
            DirectoryCache.Remove(activationAddress);
        }

        /// <summary>
        /// For testing purposes only.
        /// Returns the silo that this silo thinks is the primary owner of directory information for
        /// the provided grain ID.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        public SiloAddress? GetPrimaryForGrain(GrainId grain) => CalculateGrainDirectoryPartition(grain);

        private long RingDistanceToSuccessor() => FindSuccessor(MyAddress) is { } successor ? CalcRingDistance(MyAddress, successor) : 0;

        private static long CalcRingDistance(SiloAddress silo1, SiloAddress silo2)
        {
            const long ringSize = int.MaxValue * 2L;
            long hash1 = silo1.GetConsistentHashCode();
            long hash2 = silo2.GetConsistentHashCode();

            if (hash2 > hash1) return hash2 - hash1;
            if (hash2 < hash1) return ringSize - (hash1 - hash2);

            return 0;
        }

        internal IRemoteGrainDirectory GetDirectoryReference(SiloAddress silo)
        {
            return this.grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceType, silo);
        }

        private bool IsSiloNextInTheRing(SiloAddress siloAddr, int hash, bool excludeMySelf)
        {
            return siloAddr.GetConsistentHashCode() <= hash && (!excludeMySelf || !siloAddr.Equals(MyAddress));
        }

        private static int CompareSiloAddress(SiloAddress left, SiloAddress right)
        {
            var hashComparison = left.GetConsistentHashCode().CompareTo(right.GetConsistentHashCode());
            return hashComparison != 0 ? hashComparison : left.CompareTo(right);
        }

        public void AddOrUpdateCacheEntry(GrainId grainId, SiloAddress siloAddress) => this.DirectoryCache.AddOrUpdate(new GrainAddress { GrainId = grainId, SiloAddress = siloAddress }, 0);
        public bool TryCachedLookup(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address) => (address = GetLocalCacheData(grainId)) is not null;
        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<LocalGrainDirectory>(ServiceLifecycleStage.RuntimeServices, (ct) => Task.Run(() => Start()), (ct) => Task.Run(() => StopAsync()));
        }

        private readonly struct SiloAddressLogValue(SiloAddress silo)
        {
            public override string ToString() => silo.ToStringWithHashCode();
        }

        private readonly struct GrainHashLogValue(GrainId grain)
        {
            public override string ToString() => grain.GetUniformHashCode().ToString();
        }

        private readonly struct SiloHashLogValue(SiloAddress? silo)
        {
            public override string ToString() => silo?.GetConsistentHashCode().ToString() ?? "null";
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Start"
        )]
        private partial void LogDebugStart();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo {SiloAddress} added silo {OtherSiloAddress}"
        )]
        private partial void LogDebugSiloAddedSilo(SiloAddress siloAddress, SiloAddress otherSiloAddress);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Catalog_SiloStatusChangeNotification_Exception,
            Message = "LocalGrainDirectory has thrown an exception while handling removal of silo {Silo}."
        )]
        private partial void LogErrorSiloStatusChangeNotification(SiloAddressLogValue silo, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.Catalog_SiloStatusChangeNotification,
            Message = "LocalGrainDirectory is deactivating {Count} activations because silo {Silo} entered status {Status} and was the primary directory partition for these grain ids."
        )]
        private partial void LogInfoSiloStatusChangeNotification(int count, SiloAddressLogValue silo, SiloStatus status);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Catalog_DeactivateActivation_Exception,
            Message = "LocalGrainDirectory has thrown an exception while deactivating activation {GrainId} due to removal of silo {Silo}."
        )]
        private partial void LogErrorDeactivatingActivationForRemovedSilo(Exception exception, GrainId grainId, SiloAddressLogValue silo);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error processing cluster membership updates."
        )]
        private partial void LogErrorProcessingMembershipUpdates(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to refresh cluster membership after a directory membership update processing error."
        )]
        private partial void LogWarningRefreshingClusterMembershipFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to dispose the grain directory cache."
        )]
        private partial void LogWarningDisposeDirectoryCacheFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo {LocalSilo} removed silo {OtherSilo}"
        )]
        private partial void LogDebugSiloRemovedSilo(SiloAddress localSilo, SiloAddress otherSilo);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100201,
            Level = LogLevel.Warning,
            Message = "Got request to find predecessors of silo {SiloAddress}, which is not in the list of members"
        )]
        private partial void LogWarningFindPredecessorSiloNotInList(SiloAddress siloAddress);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100203,
            Level = LogLevel.Warning,
            Message = "Got request to find successors of silo {SiloAddress}, which is not in the list of members"
        )]
        private partial void LogWarningFindSuccessorSiloNotInList(SiloAddress siloAddress);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} looked for a system target {GrainId}, returned {TargetSilo}"
        )]
        private partial void LogTraceSystemTargetLookup(SiloAddress siloAddress, GrainId grainId, SiloAddress targetSilo);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} calculated directory partition owner silo {OwnerAddress} for grain {GrainId}: {GrainIdHash} --> {OwnerAddressHash}"
        )]
        private partial void LogTraceCalculatedDirectoryPartitionOwner(SiloAddress siloAddress, SiloAddress? ownerAddress, GrainId grainId, int grainIdHash, long? ownerAddressHash);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "RegisterAsync - It seems we are not the owner of activation {Address} (hash: {Hash:X}), trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningRegisterAsyncNotOwner(GrainAddress address, int hash, SiloAddress forwardAddress, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "UnregisterAfterNonexistingActivation addr={Address} origin={Origin}"
        )]
        private partial void LogTraceUnregisterAfterNonexistingActivation(GrainAddress address, SiloAddress origin);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "UnregisterAsync - It seems we are not the owner of activation {Address}, trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningUnregisterAsyncNotOwner(GrainAddress address, SiloAddress? forwardAddress, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "RegisterAsync - It seems we are not the owner of some activations, trying to forward it to {Count} silos (hopCount={HopCount})"
        )]
        private partial void LogWarningUnregisterManyAsyncNotOwner(int count, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo {SiloAddress} tries to lookup for {Grain}-->{PartitionOwner} ({GrainHashCode}-->{PartitionOwnerHashCode})"
        )]
        private partial void LogDebugLocalLookupAttempt(SiloAddress siloAddress, GrainId grain, SiloAddress? partitionOwner, GrainHashLogValue grainHashCode, SiloHashLogValue partitionOwnerHashCode);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "LocalLookup mine {GrainId}=null"
        )]
        private partial void LogTraceLocalLookupMineNull(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "LocalLookup cache {GrainId}={TargetAddress}"
        )]
        private partial void LogTraceLocalLookupCache(GrainId grainId, GrainAddress? targetAddress);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "LocalLookup mine {GrainId}={Address}"
        )]
        private partial void LogTraceLocalLookupMine(GrainId grainId, GrainAddress? address);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "TryFullLookup else {GrainId}=null"
        )]
        private partial void LogTraceTryFullLookupElse(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "LookupAsync - It seems we are not the owner of grain {GrainId} (hash: {Hash:X}), trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningLookupAsyncNotOwner(GrainId grainId, int hash, SiloAddress forwardAddress, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "FullLookup mine {GrainId}=none"
        )]
        private partial void LogTraceFullLookupMineNone(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "FullLookup mine {GrainId}={Address}"
        )]
        private partial void LogTraceFullLookupMine(GrainId grainId, GrainAddress? address);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "FullLookup remote {GrainId}={Address}"
        )]
        private partial void LogTraceFullLookupRemote(GrainId grainId, GrainAddress? address);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "DeleteGrainAsync - It seems we are not the owner of grain {GrainId}, trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningDeleteGrainAsyncNotOwner(GrainId grainId, SiloAddress? forwardAddress, int hopCount);

        private class DirectoryMembership(ImmutableList<SiloAddress> membershipRingList, ImmutableHashSet<SiloAddress> membershipCache)
        {
            public static DirectoryMembership Default { get; } = new DirectoryMembership([], []);

            public static DirectoryMembership Create(IEnumerable<SiloAddress> members)
            {
                var builder = ImmutableList.CreateBuilder<SiloAddress>();
                builder.AddRange(members);
                builder.Sort(CompareSiloAddress);
                var ring = builder.ToImmutable();
                return new DirectoryMembership(ring, [.. ring]);
            }

            public ImmutableList<SiloAddress> MembershipRingList { get; } = membershipRingList;
            public ImmutableHashSet<SiloAddress> MembershipCache { get; } = membershipCache;
        }
    }
}
