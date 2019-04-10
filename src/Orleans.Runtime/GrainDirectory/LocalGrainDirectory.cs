using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Configuration;

namespace Orleans.Runtime.GrainDirectory
{
    internal class LocalGrainDirectory :
        MarshalByRefObject,
        ILocalGrainDirectory
    {
        private DirectoryMembershipSnapshot membershipSnapshot;

        private readonly DedicatedAsynchAgent maintainer;
        private readonly ILogger log;
        private readonly SiloAddress seed;
        private readonly RegistrarManager registrarManager;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IMultiClusterOracle multiClusterOracle;
        private readonly IInternalGrainFactory grainFactory;
        private CatalogOnSiloRemoved catalogOnSiloRemoved;

        // Consider: move these constants into an apropriate place
        internal const int HOP_LIMIT = 3; // forward a remote request no more than two times
        public static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5); // Pause 5 seconds between forwards to let the membership directory settle down
        
        internal ILogger Logger { get { return log; } } // logger is shared with classes that manage grain directory

        internal bool Running
        {
            get => this.MembershipSnapshot.IsLocalDirectoryRunning;
            set
            {
                DirectoryMembershipSnapshot existing;
                DirectoryMembershipSnapshot updated;
                do
                {
                    existing = this.MembershipSnapshot;
                    updated = existing.WithUpdate(isLocalDirectoryRunning: value, existing.ClusterMembership);
                } while (Interlocked.CompareExchange(ref this.membershipSnapshot, updated, existing) != existing);
            }
        }

        public DirectoryMembershipSnapshot MembershipSnapshot => this.membershipSnapshot;
        internal SiloAddress MyAddress { get; private set; }

        internal IGrainDirectoryCache<IReadOnlyList<Tuple<SiloAddress, ActivationId>>> DirectoryCache { get; private set; }
        internal GrainDirectoryPartition DirectoryPartition { get; private set; }

        public RemoteGrainDirectory RemoteGrainDirectory { get; private set; }
        public RemoteGrainDirectory CacheValidator { get; private set; }
        public ClusterGrainDirectory RemoteClusterGrainDirectory { get; private set; }

        internal OrleansTaskScheduler Scheduler { get; private set; }

        internal GrainDirectoryHandoffManager HandoffManager { get; private set; }

        public string ClusterId { get; }

        internal GlobalSingleInstanceActivationMaintainer GsiActivationMaintainer { get; private set; }

        private readonly CounterStatistic localLookups;
        private readonly CounterStatistic localSuccesses;
        private readonly CounterStatistic fullLookups;
        private readonly CounterStatistic cacheLookups;
        private readonly CounterStatistic cacheSuccesses;
        private readonly CounterStatistic registrationsIssued;
        private readonly CounterStatistic registrationsSingleActIssued;
        private readonly CounterStatistic unregistrationsIssued;
        private readonly CounterStatistic unregistrationsManyIssued;
        private readonly IntValueStatistic directoryPartitionCount;

        internal readonly CounterStatistic RemoteLookupsSent;
        internal readonly CounterStatistic RemoteLookupsReceived;
        internal readonly CounterStatistic LocalDirectoryLookups;
        internal readonly CounterStatistic LocalDirectorySuccesses;
        internal readonly CounterStatistic CacheValidationsSent;
        internal readonly CounterStatistic CacheValidationsReceived;
        internal readonly CounterStatistic RegistrationsLocal;
        internal readonly CounterStatistic RegistrationsRemoteSent;
        internal readonly CounterStatistic RegistrationsRemoteReceived;
        internal readonly CounterStatistic RegistrationsSingleActLocal;
        internal readonly CounterStatistic RegistrationsSingleActRemoteSent;
        internal readonly CounterStatistic RegistrationsSingleActRemoteReceived;
        internal readonly CounterStatistic UnregistrationsLocal;
        internal readonly CounterStatistic UnregistrationsRemoteSent;
        internal readonly CounterStatistic UnregistrationsRemoteReceived;
        internal readonly CounterStatistic UnregistrationsManyRemoteSent;
        internal readonly CounterStatistic UnregistrationsManyRemoteReceived;

        public LocalGrainDirectory(
            ILocalSiloDetails siloDetails,
            OrleansTaskScheduler scheduler,
            ISiloStatusOracle siloStatusOracle,
            IMultiClusterOracle multiClusterOracle,
            IInternalGrainFactory grainFactory,
            Factory<GrainDirectoryPartition> grainDirectoryPartitionFactory,
            RegistrarManager registrarManager,
            ExecutorService executorService,
            IOptions<DevelopmentClusterMembershipOptions> developmentClusterMembershipOptions,
            IOptions<MultiClusterOptions> multiClusterOptions,
            IOptions<GrainDirectoryOptions> grainDirectoryOptions,
            ILoggerFactory loggerFactory)
        {
            this.log = loggerFactory.CreateLogger<LocalGrainDirectory>();

            var clusterId = multiClusterOptions.Value.HasMultiClusterNetwork ? siloDetails.ClusterId : null;
            MyAddress = siloDetails.SiloAddress;
            Scheduler = scheduler;
            this.siloStatusOracle = siloStatusOracle;
            this.multiClusterOracle = multiClusterOracle;
            this.grainFactory = grainFactory;
            ClusterId = clusterId;

            DirectoryCache = GrainDirectoryCacheFactory<IReadOnlyList<Tuple<SiloAddress, ActivationId>>>.CreateGrainDirectoryCache(grainDirectoryOptions.Value);
            /* TODO - investigate dynamic config changes using IOptions - jbragg
                        clusterConfig.OnConfigChange("Globals/Caching", () =>
                        {
                            lock (membershipCache)
                            {
                                DirectoryCache = GrainDirectoryCacheFactory<IReadOnlyList<Tuple<SiloAddress, ActivationId>>>.CreateGrainDirectoryCache(globalConfig);
                            }
                        });
            */
            maintainer =
                GrainDirectoryCacheFactory<IReadOnlyList<Tuple<SiloAddress, ActivationId>>>.CreateGrainDirectoryCacheMaintainer(
                    this,
                    this.DirectoryCache,
                    activations => activations.Select(a => Tuple.Create(a.Silo, a.Activation)).ToList().AsReadOnly(),
                    grainFactory, 
                    executorService,
                    loggerFactory);
            GsiActivationMaintainer = new GlobalSingleInstanceActivationMaintainer(this, this.Logger, grainFactory, multiClusterOracle, executorService, siloDetails, multiClusterOptions, loggerFactory, registrarManager);

            var primarySiloEndPoint = developmentClusterMembershipOptions.Value.PrimarySiloEndpoint;
            if (primarySiloEndPoint != null)
            {
                this.seed = this.MyAddress.Endpoint.Equals(primarySiloEndPoint) ? this.MyAddress : SiloAddress.New(primarySiloEndPoint, 0);
            }

            this.membershipSnapshot = new DirectoryMembershipSnapshot(this.log, this.MyAddress, this.seed, false, siloStatusOracle.CurrentSnapshot);

            DirectoryPartition = grainDirectoryPartitionFactory();
            HandoffManager = new GrainDirectoryHandoffManager(this, siloStatusOracle, grainFactory, grainDirectoryPartitionFactory, loggerFactory);

            RemoteGrainDirectory = new RemoteGrainDirectory(this, Constants.DirectoryServiceId, loggerFactory);
            CacheValidator = new RemoteGrainDirectory(this, Constants.DirectoryCacheValidatorId, loggerFactory);
            RemoteClusterGrainDirectory = new ClusterGrainDirectory(this, Constants.ClusterDirectoryServiceId, clusterId, grainFactory, multiClusterOracle, loggerFactory);

            Func<SiloAddress, string> siloAddressPrint = (SiloAddress addr) => 
                String.Format("{0}/{1:X}", addr.ToLongString(), addr.GetConsistentHashCode());
            
            localLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCAL_ISSUED);
            localSuccesses = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCAL_SUCCESSES);
            fullLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_FULL_ISSUED);

            RemoteLookupsSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_REMOTE_SENT);
            RemoteLookupsReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_REMOTE_RECEIVED);

            LocalDirectoryLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_ISSUED);
            LocalDirectorySuccesses = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_LOCALDIRECTORY_SUCCESSES);

            cacheLookups = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_CACHE_ISSUED);
            cacheSuccesses = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_CACHE_SUCCESSES);
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_LOOKUPS_CACHE_HITRATIO, () =>
                {
                    long delta1, delta2;
                    long curr1 = cacheSuccesses.GetCurrentValueAndDelta(out delta1);
                    long curr2 = cacheLookups.GetCurrentValueAndDelta(out delta2);
                    return String.Format("{0}, Delta={1}", 
                        (curr2 != 0 ? (float)curr1 / (float)curr2 : 0)
                        ,(delta2 !=0 ? (float)delta1 / (float)delta2 : 0));
                });

            CacheValidationsSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_VALIDATIONS_CACHE_SENT);
            CacheValidationsReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_VALIDATIONS_CACHE_RECEIVED);

            registrationsIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_ISSUED);
            RegistrationsLocal = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_LOCAL);
            RegistrationsRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_REMOTE_SENT);
            RegistrationsRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_REMOTE_RECEIVED);
            registrationsSingleActIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_ISSUED);
            RegistrationsSingleActLocal = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_LOCAL);
            RegistrationsSingleActRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_SENT);
            RegistrationsSingleActRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_REGISTRATIONS_SINGLE_ACT_REMOTE_RECEIVED);
            unregistrationsIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_ISSUED);
            UnregistrationsLocal = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_LOCAL);
            UnregistrationsRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_REMOTE_SENT);
            UnregistrationsRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_REMOTE_RECEIVED);
            unregistrationsManyIssued = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_MANY_ISSUED);
            UnregistrationsManyRemoteSent = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_SENT);
            UnregistrationsManyRemoteReceived = CounterStatistic.FindOrCreate(StatisticNames.DIRECTORY_UNREGISTRATIONS_MANY_REMOTE_RECEIVED);

            directoryPartitionCount = IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_PARTITION_SIZE, () => DirectoryPartition.Count);
            IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_RINGDISTANCE, () => RingDistanceToSuccessor());
            FloatValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_RINGPERCENTAGE, () => (((float)this.RingDistanceToSuccessor()) / ((float)(int.MaxValue * 2L))) * 100);
            FloatValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE, () => this.membershipSnapshot.Ring.Length == 0 ? 0 : ((float)100 / (float)this.membershipSnapshot.Ring.Length));
            IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_RINGSIZE, () => this.membershipSnapshot.Ring.Length);
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING, () =>
                {
                    return Utils.EnumerableToString(this.membershipSnapshot.Ring, siloAddressPrint);
                });
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_PREDECESSORS, () => Utils.EnumerableToString(this.membershipSnapshot.FindPredecessors(this.MyAddress, 1), siloAddressPrint));
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_SUCCESSORS, () => Utils.EnumerableToString(this.membershipSnapshot.FindSuccessors(this.MyAddress, 1), siloAddressPrint));

            this.registrarManager = registrarManager;
        }

        public void Start()
        {
            log.Info("Start");
            Running = true;

            if (maintainer != null)
            {
                maintainer.Start();
            }
            if (GsiActivationMaintainer != null)
            {
                GsiActivationMaintainer.Start();
            }
        }

        public void OnRuntimeServicesStart()
        {
            this.Scheduler.QueueTask(this.ProcessMembershipChanges, this.CacheValidator.SchedulingContext);
        }

        private async Task ProcessMembershipChanges()
        {
            var notification = this.siloStatusOracle.MembershipUpdates;
            while (this.Running)
            {
                try
                {
                    notification = await notification.NextAsync();
                    if (!notification.HasValue)
                    {
                        continue;
                    }

                    // Update the membership snapshot
                    var update = notification.Value;
                    DirectoryMembershipSnapshot existing;
                    DirectoryMembershipSnapshot updated;
                    Dictionary<GrainId, IGrainInfo> directoryPartitionCopy;
                    IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> directoryCache;
                    do
                    {
                        existing = this.membershipSnapshot;

                        if (existing.ClusterMembership.Version >= update.Snapshot.Version)
                        {
                            // we have already removed this silo
                            return;
                        }

                        updated = existing.WithUpdate(existing.IsLocalDirectoryRunning, update.Snapshot);
                        directoryPartitionCopy = this.DirectoryPartition.GetItems();
                        directoryCache = this.DirectoryCache.KeyValues;
                    } while (Interlocked.CompareExchange(ref this.membershipSnapshot, updated, existing) != existing);

                    // Process the individual changes
                    foreach (var change in update.Changes)
                    {
                        // Ignore changes for the local silo
                        if (change.SiloAddress.Equals(this.MyAddress)) continue;

                        var status = change.Status;
                        if (status.IsTerminating())
                        {
                            RemoveSilo(existing, updated, change, directoryPartitionCopy, directoryCache);
                        }
                        else if (status == SiloStatus.Active)
                        {
                            AddSilo(updated, change, directoryPartitionCopy, directoryCache);
                        }

                        if (log.IsEnabled(LogLevel.Information)) this.log.LogInformation("Silo {LocalSilo} removed silo {RemoteSilo}", this.MyAddress, change.SiloAddress);
                    }
                }
                catch (Exception exception)
                {
                    this.Logger.LogWarning("Exception while processing membership updates: {Exception}", exception);
                }
            }

            void AddSilo(
                DirectoryMembershipSnapshot updated,
                ClusterMember added,
                Dictionary<GrainId, IGrainInfo> directoryPartitionCopy,
                IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> directoryCache)
            {
                if (log.IsEnabled(LogLevel.Information)) log.LogInformation("Silo {LocalSilo} adding silo {RemoteSilo}", MyAddress, added.SiloAddress);
                HandoffManager.ProcessSiloAddEvent(updated, added.SiloAddress);

                this.AdjustLocalDirectory(directoryPartitionCopy, added.SiloAddress, dead: false);
                this.AdjustLocalCache(updated, directoryCache, added.SiloAddress, dead: false);
            }

            void RemoveSilo(
                DirectoryMembershipSnapshot existing,
                DirectoryMembershipSnapshot updated,
                ClusterMember removed,
                Dictionary<GrainId, IGrainInfo> directoryPartitionCopy,
                IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> directoryCache)
            {

                if (log.IsEnabled(LogLevel.Information))
                {
                    this.log.LogInformation("Silo {LocalSilo} removing silo {RemoteSilo}", this.MyAddress, removed.SiloAddress);
                }

                try
                {
                    // Only notify the catalog once.
                    // The catalog is intentionally called using the previous membership snapshot so that calculations about directory partitions
                    // are consistent.
                    this.catalogOnSiloRemoved?.Invoke(existing, removed.SiloAddress, removed.Status);
                }
                catch (Exception exc)
                {
                    this.log.Error(
                        ErrorCode.Directory_SiloStatusChangeNotification_Exception,
                        string.Format("CatalogSiloStatusListener.RemoveServer has thrown an exception when notified about removed silo {0}.", removed.SiloAddress.ToStringWithHashCode()), exc);
                }

                this.HandoffManager.ProcessSiloRemoveEvent(updated, removed.SiloAddress);
                this.AdjustLocalDirectory(directoryPartitionCopy, removed.SiloAddress, dead: true);
                this.AdjustLocalCache(updated, directoryCache, removed.SiloAddress, dead: true);
            }
        }

        // Note that this implementation stops processing directory change requests (Register, Unregister, etc.) when the Stop event is raised. 
        // This means that there may be a short period during which no silo believes that it is the owner of directory information for a set of 
        // grains (for update purposes), which could cause application requests that require a new activation to be created to time out. 
        // The alternative would be to allow the silo to process requests after it has handed off its partition, in which case those changes 
        // would receive successful responses but would not be reflected in the eventual state of the directory. 
        // It's easy to change this, if we think the trade-off is better the other way.
        public async Task Stop(bool doOnStopHandoff)
        {
            // This will cause remote write requests to be forwarded to the silo that will become the new owner.
            // Requests might bounce back and forth for a while as membership stabilizes, but they will either be served by the
            // new owner of the grain, or will wind up failing. In either case, we avoid requests succeeding at this silo after we've
            // begun stopping, which could cause them to not get handed off to the new owner.

            //mark Running as false will exclude myself from CalculateGrainDirectoryPartition(grainId)
            Running = false;

            if (maintainer != null)
            {
                maintainer.Stop();
            }
            if (GsiActivationMaintainer != null)
            {
                GsiActivationMaintainer.Stop();
            }

            if (doOnStopHandoff)
            {
                try
                {
                    await HandoffManager.ProcessSiloStoppingEvent(this.membershipSnapshot);
                }
                catch (Exception exc)
                {
                    this.log.LogWarning($"GrainDirectoryHandOffManager failed ProcessSiloStoppingEvent due to exception {exc}");
                }
            }
            DirectoryPartition.Clear();
            DirectoryCache.Clear();
        }

        /// <inheritdoc />
        public void SetSiloRemovedCatalogCallback(CatalogOnSiloRemoved callback)
        {
            this.catalogOnSiloRemoved = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        /// <summary>
        /// Adjust local directory following the addition/removal of a silo
        /// </summary>
        private void AdjustLocalDirectory(Dictionary<GrainId, IGrainInfo> directoryPartitionCopy, SiloAddress silo, bool dead)
        {
            // Determine which activations to remove.
            var activationsToRemove = new List<(GrainId, ActivationId)>();
            foreach (var entry in this.DirectoryPartition.GetItems())
            {
                var (grain, grainInfo) = (entry.Key, entry.Value);
                foreach (var instance in grainInfo.Instances)
                {
                    var (activationId, activationInfo) = (instance.Key, instance.Value);

                    // Include any activations from dead silos and from predecessors.
                    var siloIsDead = dead && activationInfo.SiloAddress.Equals(silo);
                    var siloIsPredecessor = activationInfo.SiloAddress.IsPredecessorOf(silo);
                    if (siloIsDead || siloIsPredecessor)
                    {
                        activationsToRemove.Add((grain, activationId));
                    }
                }
            }
            // drop all records of activations located on the removed silo
            foreach (var activation in activationsToRemove)
            {
                DirectoryPartition.RemoveActivation(activation.Item1, activation.Item2);
            }
        }

        /// Adjust local cache following the removal of a silo by dropping:
        /// 1) entries that point to activations located on the removed silo 
        /// 2) entries for grains that are now owned by this silo (me)
        /// 3) entries for grains that were owned by this removed silo - we currently do NOT do that.
        ///     If we did 3, we need to do that BEFORE we change the membershipRingList (based on old Membership).
        ///     We don't do that since first cache refresh handles that. 
        ///     Second, since Membership events are not guaranteed to be ordered, we may remove a cache entry that does not really point to a failed silo.
        ///     To do that properly, we need to store for each cache entry who was the directory owner that registered this activation (the original partition owner). 
        private void AdjustLocalCache(DirectoryMembershipSnapshot snapshot, IReadOnlyList<Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int>> cache, SiloAddress silo, bool dead)
        {
            // For dead silos, remove any activation registered to that silo or one of its predecessors.
            // For new silos, remove any activation registered to one of its predecessors.
            Func<Tuple<SiloAddress, ActivationId>, bool> predicate;
            if (dead) predicate = t => t.Item1.Equals(silo) || t.Item1.IsPredecessorOf(silo);
            else predicate = t => t.Item1.IsPredecessorOf(silo);

            // remove all records of activations located on the removed silo
            foreach (Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int> tuple in cache)
            {
                // 2) remove entries now owned by me (they should be retrieved from my directory partition)
                if (MyAddress.Equals(snapshot.CalculateGrainDirectoryPartition(tuple.Item1)))
                {
                    DirectoryCache.Remove(tuple.Item1);
                }

                // 1) remove entries that point to activations located on the removed silo
                RemoveActivations(DirectoryCache, tuple.Item1, tuple.Item2, tuple.Item3, predicate);
            }
        }

        private bool IsValidSilo(SiloAddress silo)
        { 
            return this.siloStatusOracle.IsFunctionalDirectory(silo);
        }

        public SiloAddress CheckIfShouldForward(GrainId grainId, int hopCount, string operationDescription)
        {
            SiloAddress owner = this.membershipSnapshot.CalculateGrainDirectoryPartition(grainId);

            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }

            if (owner.Equals(MyAddress))
            {
                // if I am the owner, perform the operation locally
                return null;
            }

            if (hopCount >= HOP_LIMIT)
            {
                // we are not forwarding because there were too many hops already
                throw new OrleansException(string.Format("Silo {0} is not owner of {1}, cannot forward {2} to owner {3} because hop limit is reached", MyAddress, grainId, operationDescription, owner));
            }

            // forward to the silo that we think is the owner
            return owner;
        }


        public async Task<AddressAndTag> RegisterAsync(ActivationAddress address, bool singleActivation, int hopCount)
        {
            var counterStatistic = 
                singleActivation 
                ? (hopCount > 0 ? this.RegistrationsSingleActRemoteReceived : this.registrationsSingleActIssued)
                : (hopCount > 0 ? this.RegistrationsRemoteReceived : this.registrationsIssued);

            counterStatistic.Increment();

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(address.Grain, hopCount, "RegisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(address.Grain, hopCount, "RegisterAsync(recheck)");
            }

            if (forwardAddress == null)
            {
                (singleActivation ? RegistrationsSingleActLocal : RegistrationsLocal).Increment();

                // we are the owner     
                var registrar = this.registrarManager.GetRegistrarForGrain(address.Grain);

                if (log.IsEnabled(LogLevel.Trace)) log.Trace($"use registrar {registrar.GetType().Name} for activation {address}");

                return registrar.IsSynchronous ? registrar.Register(address, singleActivation)
                    : await registrar.RegisterAsync(address, singleActivation);
            }
            else
            {
                (singleActivation ? RegistrationsSingleActRemoteSent : RegistrationsRemoteSent).Increment();

                // otherwise, notify the owner
                AddressAndTag result = await GetDirectoryReference(forwardAddress).RegisterAsync(address, singleActivation, hopCount + 1);

                if (singleActivation)
                {
                    // Caching optimization: 
                    // cache the result of a successfull RegisterSingleActivation call, only if it is not a duplicate activation.
                    // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                    if (result.Address == null) return result;

                    if (!address.Equals(result.Address) || !IsValidSilo(address.Silo)) return result;

                    var cached = new List<Tuple<SiloAddress, ActivationId>>(1) { Tuple.Create(address.Silo, address.Activation) };
                    // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                    DirectoryCache.AddOrUpdate(address.Grain, cached, result.VersionTag);
                }
                else
                {
                    if (IsValidSilo(address.Silo))
                    {
                        // Caching optimization:
                        // cache the result of a successfull RegisterActivation call, only if it is not a duplicate activation.
                        // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                        IReadOnlyList<Tuple<SiloAddress, ActivationId>> cached;
                        if (!DirectoryCache.LookUp(address.Grain, out cached))
                        {
                            cached = new List<Tuple<SiloAddress, ActivationId>>(1)
                        {
                            Tuple.Create(address.Silo, address.Activation)
                        };
                        }
                        else
                        {
                            var newcached = new List<Tuple<SiloAddress, ActivationId>>(cached.Count + 1);
                            newcached.AddRange(cached);
                            newcached.Add(Tuple.Create(address.Silo, address.Activation));
                            cached = newcached;
                        }
                        // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                        DirectoryCache.AddOrUpdate(address.Grain, cached, result.VersionTag);
                    }
                }

                return result;
            }
        }

        public Task UnregisterAfterNonexistingActivation(ActivationAddress addr, SiloAddress origin)
        {
            log.Trace("UnregisterAfterNonexistingActivation addr={0} origin={1}", addr, origin);

            if (origin == null || this.membershipSnapshot.Members.Contains(origin))
            {
                // the request originated in this cluster, call unregister here
                return UnregisterAsync(addr, UnregistrationCause.NonexistentActivation, 0);
            }
            else
            {
                // the request originated in another cluster, call unregister there
                var remoteDirectory = GetDirectoryReference(origin);
                return remoteDirectory.UnregisterAsync(addr, UnregistrationCause.NonexistentActivation);
            }
        }

        public async Task UnregisterAsync(ActivationAddress address, UnregistrationCause cause, int hopCount)
        {
            (hopCount > 0 ? UnregistrationsRemoteReceived : unregistrationsIssued).Increment();

            if (hopCount == 0)
                InvalidateCacheEntry(address);

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardaddress = this.CheckIfShouldForward(address.Grain, hopCount, "UnregisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardaddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardaddress = this.CheckIfShouldForward(address.Grain, hopCount, "UnregisterAsync(recheck)");
            }

            if (forwardaddress == null)
            {
                // we are the owner
                UnregistrationsLocal.Increment();

                var registrar = this.registrarManager.GetRegistrarForGrain(address.Grain);

                if (registrar.IsSynchronous)
                    registrar.Unregister(address, cause);
                else
                    await registrar.UnregisterAsync(new List<ActivationAddress>() { address }, cause);
            }
            else
            {
                UnregistrationsRemoteSent.Increment();
                // otherwise, notify the owner
                await GetDirectoryReference(forwardaddress).UnregisterAsync(address, cause, hopCount + 1);
            }
        }

        private void AddToDictionary<K,V>(ref Dictionary<K, List<V>> dictionary, K key, V value)
        {
            if (dictionary == null) 
               dictionary = new Dictionary<K,List<V>>();
            List<V> list;
            if (! dictionary.TryGetValue(key, out list))
               dictionary[key] = list = new List<V>();
            list.Add(value);
        }


        // helper method to avoid code duplication inside UnregisterManyAsync
        private void UnregisterOrPutInForwardList(IEnumerable<ActivationAddress> addresses, UnregistrationCause cause, int hopCount,
            ref Dictionary<SiloAddress, List<ActivationAddress>> forward, List<Task> tasks, string context)
        {
            Dictionary<IGrainRegistrar, List<ActivationAddress>> unregisterBatches = new Dictionary<IGrainRegistrar, List<ActivationAddress>>();

            foreach (var address in addresses)
            {
                // see if the owner is somewhere else (returns null if we are owner)
                var forwardAddress = this.CheckIfShouldForward(address.Grain, hopCount, context);

                if (forwardAddress != null)
                {
                    AddToDictionary(ref forward, forwardAddress, address);
                }
                else
                {
                    // we are the owner
                    UnregistrationsLocal.Increment();
                    var registrar = this.registrarManager.GetRegistrarForGrain(address.Grain);

                    if (registrar.IsSynchronous)
                    {
                        registrar.Unregister(address, cause);
                    }
                    else
                    {
                        List<ActivationAddress> list;
                        if (!unregisterBatches.TryGetValue(registrar, out list))
                            unregisterBatches.Add(registrar, list = new List<ActivationAddress>());
                        list.Add(address);
                    }
                }
            }

            // batch-unregister for each asynchronous registrar
            foreach (var kvp in unregisterBatches)
            {
                tasks.Add(kvp.Key.UnregisterAsync(kvp.Value, cause));
            }
        }


        public async Task UnregisterManyAsync(List<ActivationAddress> addresses, UnregistrationCause cause, int hopCount)
        {
            (hopCount > 0 ? UnregistrationsManyRemoteReceived : unregistrationsManyIssued).Increment();

            Dictionary<SiloAddress, List<ActivationAddress>> forwardlist = null;
            var tasks = new List<Task>();

            UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist, tasks, "UnregisterManyAsync");

            // before forwarding to other silos, we insert a retry delay and re-check destination
            if (hopCount > 0 && forwardlist != null)
            {
                await Task.Delay(RETRY_DELAY);
                Dictionary<SiloAddress, List<ActivationAddress>> forwardlist2 = null;
                UnregisterOrPutInForwardList(forwardlist.SelectMany(kvp => kvp.Value), cause, hopCount, ref forwardlist2, tasks, "UnregisterManyAsync(recheck)");
                forwardlist = forwardlist2;
            }

            // forward the requests
            if (forwardlist != null)
            {
                foreach (var kvp in forwardlist)
                {
                    UnregistrationsManyRemoteSent.Increment();
                    tasks.Add(GetDirectoryReference(kvp.Key).UnregisterManyAsync(kvp.Value, cause, hopCount + 1));
                }
            }

            // wait for all the requests to finish
            await Task.WhenAll(tasks);
        }


        public bool LocalLookup(GrainId grain, out AddressesAndTag result)
        {
            localLookups.Increment();

            SiloAddress silo = this.membershipSnapshot.CalculateGrainDirectoryPartition(grain);


            if (log.IsEnabled(LogLevel.Debug)) log.Debug("Silo {0} tries to lookup for {1}-->{2} ({3}-->{4})", MyAddress, grain, silo, grain.GetUniformHashCode(), silo?.GetConsistentHashCode());

            //this will only happen if I'm the only silo in the cluster and I'm shutting down
            if (silo == null)
            {
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup mine {0}=null", grain);
                result = new AddressesAndTag();
                return false;
            }

            // check if we own the grain
            if (silo.Equals(MyAddress))
            {
                LocalDirectoryLookups.Increment();
                result = GetLocalDirectoryData(grain);
                if (result.Addresses == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were 
                    // some recent changes in the membership
                    if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup mine {0}=null", grain);
                    return false;
                }
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup mine {0}={1}", grain, result.Addresses.ToStrings());
                LocalDirectorySuccesses.Increment();
                localSuccesses.Increment();
                return true;
            }

            // handle cache
            result = new AddressesAndTag();
            cacheLookups.Increment();
            result.Addresses = GetLocalCacheData(grain);
            if (result.Addresses == null)
            {
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("TryFullLookup else {0}=null", grain);
                return false;
            }
            if (log.IsEnabled(LogLevel.Trace)) log.Trace("LocalLookup cache {0}={1}", grain, result.Addresses.ToStrings());
            cacheSuccesses.Increment();
            localSuccesses.Increment();
            return true;
        }

        public AddressesAndTag GetLocalDirectoryData(GrainId grain)
        {
            return DirectoryPartition.LookUpActivations(grain);
        }

        public List<ActivationAddress> GetLocalCacheData(GrainId grain)
        {
            IReadOnlyList<Tuple<SiloAddress, ActivationId>> cached;
            return DirectoryCache.LookUp(grain, out cached) ? 
                cached.Select(elem => ActivationAddress.GetAddress(elem.Item1, grain, elem.Item2)).Where(addr => IsValidSilo(addr.Silo)).ToList() : 
                null;
        }

        public Task<AddressesAndTag> LookupInCluster(GrainId grainId, string clusterId)
        {
            if (clusterId == null) throw new ArgumentNullException(nameof(clusterId));

            if (clusterId == ClusterId)
            {
                return LookupAsync(grainId);
            }
            else
            {
                // find gateway
                var gossipOracle = this.multiClusterOracle;
                var clusterGatewayAddress = gossipOracle.GetRandomClusterGateway(clusterId);
                if (clusterGatewayAddress != null)
                {
                    // call remote grain directory
                    var remotedirectory = GetDirectoryReference(clusterGatewayAddress);
                    return remotedirectory.LookupAsync(grainId);
                }
                else
                {
                    return Task.FromResult(default(AddressesAndTag));
                }
            }
        }

        public async Task<AddressesAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
        {
            (hopCount > 0 ? RemoteLookupsReceived : fullLookups).Increment();

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "LookUpAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "LookUpAsync(recheck)");
            }

            if (forwardAddress == null)
            {
                // we are the owner
                LocalDirectoryLookups.Increment();
                var localResult = DirectoryPartition.LookUpActivations(grainId);
                if (localResult.Addresses == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were 
                    // some recent changes in the membership
                    if (log.IsEnabled(LogLevel.Trace)) log.Trace("FullLookup mine {0}=none", grainId);
                    localResult.Addresses = new List<ActivationAddress>();
                    localResult.VersionTag = GrainInfo.NO_ETAG;
                    return localResult;
                }

                if (log.IsEnabled(LogLevel.Trace)) log.Trace("FullLookup mine {0}={1}", grainId, localResult.Addresses.ToStrings());
                LocalDirectorySuccesses.Increment();
                return localResult;
            }
            else
            {
                // Just a optimization. Why sending a message to someone we know is not valid.
                if (!IsValidSilo(forwardAddress))
                {
                    throw new OrleansException(String.Format("Current directory at {0} is not stable to perform the lookup for grainId {1} (it maps to {2}, which is not a valid silo). Retry later.", MyAddress, grainId, forwardAddress));
                }

                RemoteLookupsSent.Increment();
                var result = await GetDirectoryReference(forwardAddress).LookupAsync(grainId, hopCount + 1);

                // update the cache
                result.Addresses = result.Addresses.Where(t => IsValidSilo(t.Silo)).ToList();
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("FullLookup remote {0}={1}", grainId, result.Addresses.ToStrings());

                var entries = result.Addresses.Select(t => Tuple.Create(t.Silo, t.Activation)).ToList();

                if (entries.Count > 0)
                    DirectoryCache.AddOrUpdate(grainId, entries, result.VersionTag);

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
                forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "DeleteGrainAsync(recheck)");
            }

            if (forwardAddress == null)
            {
                // we are the owner
                var registrar = this.registrarManager.GetRegistrarForGrain(grainId);

                if (registrar.IsSynchronous)
                    registrar.Delete(grainId);
                else
                    await registrar.DeleteAsync(grainId);
            }
            else
            {
                // otherwise, notify the owner
                DirectoryCache.Remove(grainId);
                await GetDirectoryReference(forwardAddress).DeleteGrainAsync(grainId, hopCount + 1);
            }
        }

        public void InvalidateCacheEntry(ActivationAddress activationAddress, bool invalidateDirectoryAlso = false)
        {
            int version;
            IReadOnlyList<Tuple<SiloAddress, ActivationId>> list;
            var grainId = activationAddress.Grain;
            var activationId = activationAddress.Activation;

            // look up grainId activations
            if (DirectoryCache.LookUp(grainId, out list, out version))
            {
                RemoveActivations(DirectoryCache, grainId, list, version, t => t.Item2.Equals(activationId));
            }

            // for multi-cluster registration, the local directory may cache remote activations
            // and we need to remove them here, on the fast path, to avoid forwarding the message
            // to the wrong destination again
            if (invalidateDirectoryAlso && MyAddress.Equals(this.membershipSnapshot.CalculateGrainDirectoryPartition(grainId)))
            {
                var registrar = this.registrarManager.GetRegistrarForGrain(grainId);
                registrar.InvalidateCache(activationAddress);
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            long localLookupsDelta;
            long localLookupsCurrent = localLookups.GetCurrentValueAndDelta(out localLookupsDelta);
            long localLookupsSucceededDelta;
            long localLookupsSucceededCurrent = localSuccesses.GetCurrentValueAndDelta(out localLookupsSucceededDelta);
            long fullLookupsDelta;
            long fullLookupsCurrent = fullLookups.GetCurrentValueAndDelta(out fullLookupsDelta);
            long directoryPartitionSize = directoryPartitionCount.GetCurrentValue();

            sb.AppendLine("Local Grain Directory:");
            sb.AppendFormat("   Local partition: {0} entries", directoryPartitionSize).AppendLine();
            sb.AppendLine("   Since last call:");
            sb.AppendFormat("      Local lookups: {0}", localLookupsDelta).AppendLine();
            sb.AppendFormat("      Local found: {0}", localLookupsSucceededDelta).AppendLine();
            if (localLookupsDelta > 0)
                sb.AppendFormat("      Hit rate: {0:F1}%", (100.0 * localLookupsSucceededDelta) / localLookupsDelta).AppendLine();
            
            sb.AppendFormat("      Full lookups: {0}", fullLookupsDelta).AppendLine();
            sb.AppendLine("   Since start:");
            sb.AppendFormat("      Local lookups: {0}", localLookupsCurrent).AppendLine();
            sb.AppendFormat("      Local found: {0}", localLookupsSucceededCurrent).AppendLine();
            if (localLookupsCurrent > 0)
                sb.AppendFormat("      Hit rate: {0:F1}%", (100.0 * localLookupsSucceededCurrent) / localLookupsCurrent).AppendLine();
            
            sb.AppendFormat("      Full lookups: {0}", fullLookupsCurrent).AppendLine();
            sb.Append(DirectoryCache.ToString());

            return sb.ToString();
        }

        private long RingDistanceToSuccessor()
        {
            var snapshot = this.membershipSnapshot;
            long distance;
            List<SiloAddress> successorList = snapshot.FindSuccessors(MyAddress, 1);
            if (successorList == null || successorList.Count == 0)
            {
                distance = 0;
            }
            else
            {
                SiloAddress successor = successorList.First();
                distance = successor == null ? 0 : CalcRingDistance(MyAddress, successor);
            }
            return distance;
        }

        private string RingDistanceToSuccessor_2()
        {
            const long ringSize = int.MaxValue * 2L;
            var snapshot = this.membershipSnapshot;
            long distance;
            List<SiloAddress> successorList = snapshot.FindSuccessors(MyAddress, 1);
            if (successorList == null || successorList.Count == 0)
            {
                distance = 0;
            }
            else
            {
                SiloAddress successor = successorList.First();
                distance = successor == null ? 0 : CalcRingDistance(MyAddress, successor);
            }
            double averageRingSpace = snapshot.Ring.Length == 0 ? 0 : (1.0 / snapshot.Ring.Length);
            return string.Format("RingDistance={0:X}, %Ring Space {1:0.00000}%, Average %Ring Space {2:0.00000}%", 
                distance, ((double)distance / (double)ringSize) * 100.0, averageRingSpace * 100.0);
        }

        private static long CalcRingDistance(SiloAddress silo1, SiloAddress silo2)
        {
            const long ringSize = int.MaxValue * 2L;
            long hash1 = silo1.GetConsistentHashCode();
            long hash2 = silo2.GetConsistentHashCode();

            if (hash2 > hash1) return hash2 - hash1;
            if (hash2 < hash1) return ringSize - (hash1 - hash2);

            return 0;
        }

        public string RingStatusToString()
        {
            var snapshot = this.membershipSnapshot;
            var sb = new StringBuilder();

            sb.AppendFormat("Silo address is {0}, silo consistent hash is {1:X}.", MyAddress, MyAddress.GetConsistentHashCode()).AppendLine();
            sb.AppendLine("Ring is:");
            foreach (var silo in snapshot.Ring)
                sb.AppendFormat("    Silo {0}, consistent hash is {1:X}", silo, silo.GetConsistentHashCode()).AppendLine();

            sb.AppendFormat("My predecessors: {0}", snapshot.FindPredecessors(MyAddress, 1).ToStrings(addr => String.Format("{0}/{1:X}---", addr, addr.GetConsistentHashCode()), " -- ")).AppendLine();
            sb.AppendFormat("My successors: {0}", snapshot.FindSuccessors(MyAddress, 1).ToStrings(addr => String.Format("{0}/{1:X}---", addr, addr.GetConsistentHashCode()), " -- "));
            return sb.ToString();
        }

        internal IRemoteGrainDirectory GetDirectoryReference(SiloAddress silo)
        {
            return this.grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceId, silo);
        }
        
        private static void RemoveActivations(IGrainDirectoryCache<IReadOnlyList<Tuple<SiloAddress, ActivationId>>> directoryCache, GrainId key, IReadOnlyList<Tuple<SiloAddress, ActivationId>> activations, int version, Func<Tuple<SiloAddress, ActivationId>, bool> doRemove)
        {
            int removeCount = activations.Count(doRemove);
            if (removeCount == 0)
            {
                return; // nothing to remove, done here
            }

            if (activations.Count > removeCount) // still some left, update activation list.  Note: Most of the time there should be only one activation
            {
                var newList = new List<Tuple<SiloAddress, ActivationId>>(activations.Count - removeCount);
                newList.AddRange(activations.Where(t => !doRemove(t)));
                directoryCache.AddOrUpdate(key, newList, version);
            }
            else // no activations left, remove from cache
            {
                directoryCache.Remove(key);
            }
        }

        public bool IsSiloInCluster(SiloAddress silo)
        {
            return this.membershipSnapshot.Members.Contains(silo);
        }
    }
}
