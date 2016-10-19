using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.SystemTargetInterfaces;

namespace Orleans.Runtime.GrainDirectory
{
    internal class LocalGrainDirectory :
#if !NETSTANDARD
        MarshalByRefObject,
#endif
        ILocalGrainDirectory, ISiloStatusListener
    {
        /// <summary>
        /// list of silo members sorted by the hash value of their address
        /// </summary>
        private readonly List<SiloAddress> membershipRingList;
        private readonly HashSet<SiloAddress> membershipCache;
        private readonly AsynchAgent maintainer;
        private readonly Logger log;
        private readonly SiloAddress seed;
        internal ISiloStatusOracle Membership;

        // Consider: move these constants into an apropriate place
        internal const int HOP_LIMIT = 3; // forward a remote request no more than two times
        public static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5); // Pause 5 seconds between forwards to let the membership directory settle down

        protected SiloAddress Seed { get { return seed; } }

        internal Logger Logger { get { return log; } } // logger is shared with classes that manage grain directory

        internal bool Running;

        internal SiloAddress MyAddress { get; private set; }

        internal IGrainDirectoryCache<IReadOnlyList<Tuple<SiloAddress, ActivationId>>> DirectoryCache { get; private set; }
        internal GrainDirectoryPartition DirectoryPartition { get; private set; }

        public RemoteGrainDirectory RemoteGrainDirectory { get; private set; }
        public RemoteGrainDirectory CacheValidator { get; private set; }
        public ClusterGrainDirectory RemoteClusterGrainDirectory { get; private set; }

        private readonly TaskCompletionSource<bool> stopPreparationResolver;
        public Task StopPreparationCompletion { get { return stopPreparationResolver.Task; } }

        internal OrleansTaskScheduler Scheduler { get; private set; }

        internal GrainDirectoryHandoffManager HandoffManager { get; private set; }

        public string ClusterId { get; }

        internal ISiloStatusListener CatalogSiloStatusListener { get; set; }

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

        
        public LocalGrainDirectory(Silo silo)
        {
            log = LogManager.GetLogger("Orleans.GrainDirectory.LocalGrainDirectory");

            MyAddress = silo.LocalMessageCenter.MyAddress;
            Scheduler = silo.LocalScheduler;
            membershipRingList = new List<SiloAddress>();
            membershipCache = new HashSet<SiloAddress>();
            ClusterId = silo.ClusterId;

            silo.OrleansConfig.OnConfigChange("Globals/Caching", () =>
            {
                lock (membershipCache)
                {
                    DirectoryCache = GrainDirectoryCacheFactory<IReadOnlyList<Tuple<SiloAddress, ActivationId>>>.CreateGrainDirectoryCache(silo.GlobalConfig);
                }
            });
            maintainer = GrainDirectoryCacheFactory<IReadOnlyList<Tuple<SiloAddress, ActivationId>>>.CreateGrainDirectoryCacheMaintainer(this, DirectoryCache);
            GsiActivationMaintainer = new GlobalSingleInstanceActivationMaintainer(this, this.Logger, silo.GlobalConfig);

            if (silo.GlobalConfig.SeedNodes.Count > 0)
            {
                seed = silo.GlobalConfig.SeedNodes.Contains(MyAddress.Endpoint) ? MyAddress : SiloAddress.New(silo.GlobalConfig.SeedNodes[0], 0);
            }

            stopPreparationResolver = new TaskCompletionSource<bool>();
            DirectoryPartition = new GrainDirectoryPartition();
            HandoffManager = new GrainDirectoryHandoffManager(this, silo.GlobalConfig);

            RemoteGrainDirectory = new RemoteGrainDirectory(this, Constants.DirectoryServiceId);
            CacheValidator = new RemoteGrainDirectory(this, Constants.DirectoryCacheValidatorId);
            RemoteClusterGrainDirectory = new ClusterGrainDirectory(this, Constants.ClusterDirectoryServiceId, silo.ClusterId);

            // add myself to the list of members
            AddServer(MyAddress);

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
            FloatValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_RINGPERCENTAGE, () => (((float)RingDistanceToSuccessor()) / ((float)(int.MaxValue * 2L))) * 100);
            FloatValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_MYPORTION_AVERAGERINGPERCENTAGE, () => membershipRingList.Count == 0 ? 0 : ((float)100 / (float)membershipRingList.Count));
            IntValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_RINGSIZE, () => membershipRingList.Count);
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING, () =>
                {
                    lock (membershipCache)
                    {
                        return Utils.EnumerableToString(membershipRingList, siloAddressPrint);
                    }
                });
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_PREDECESSORS, () => Utils.EnumerableToString(FindPredecessors(MyAddress, 1), siloAddressPrint));
            StringValueStatistic.FindOrCreate(StatisticNames.DIRECTORY_RING_SUCCESSORS, () => Utils.EnumerableToString(FindSuccessors(MyAddress, 1), siloAddressPrint));
        }

        public void Start()
        {
            log.Info("Start (SeverityLevel={0})", log.SeverityLevel);
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

        // Note that this implementation stops processing directory change requests (Register, Unregister, etc.) when the Stop event is raised. 
        // This means that there may be a short period during which no silo believes that it is the owner of directory information for a set of 
        // grains (for update purposes), which could cause application requests that require a new activation to be created to time out. 
        // The alternative would be to allow the silo to process requests after it has handed off its partition, in which case those changes 
        // would receive successful responses but would not be reflected in the eventual state of the directory. 
        // It's easy to change this, if we think the trade-off is better the other way.
        public void Stop(bool doOnStopHandoff)
        {
            // This will cause remote write requests to be forwarded to the silo that will become the new owner.
            // Requests might bounce back and forth for a while as membership stabilizes, but they will either be served by the
            // new owner of the grain, or will wind up failing. In either case, we avoid requests succeeding at this silo after we've
            // begun stopping, which could cause them to not get handed off to the new owner.
            Running = false;

            if (doOnStopHandoff)
            {
                HandoffManager.ProcessSiloStoppingEvent();
            }
            else
            {
                MarkStopPreparationCompleted();
            }
            if (maintainer != null)
            {
                maintainer.Stop();
            }
            DirectoryCache.Clear();
        }

        internal void MarkStopPreparationCompleted()
        {
            stopPreparationResolver.TrySetResult(true);
        }

        internal void MarkStopPreparationFailed(Exception ex)
        {
            stopPreparationResolver.TrySetException(ex);
        }

        #region Handling membership events
        protected void AddServer(SiloAddress silo)
        {
            lock (membershipCache)
            {
                if (membershipCache.Contains(silo))
                {
                    // we have already cached this silo
                    return;
                }

                membershipCache.Add(silo);

                // insert new silo in the sorted order
                long hash = silo.GetConsistentHashCode();

                // Find the last silo with hash smaller than the new silo, and insert the latter after (this is why we have +1 here) the former.
                // Notice that FindLastIndex might return -1 if this should be the first silo in the list, but then
                // 'index' will get 0, as needed.
                int index = membershipRingList.FindLastIndex(siloAddr => siloAddr.GetConsistentHashCode() < hash) + 1;
                membershipRingList.Insert(index, silo);

                HandoffManager.ProcessSiloAddEvent(silo);

                if (log.IsVerbose) log.Verbose("Silo {0} added silo {1}", MyAddress, silo);
            }
        }

        protected void RemoveServer(SiloAddress silo, SiloStatus status)
        {
            lock (membershipCache)
            {
                if (!membershipCache.Contains(silo))
                {
                    // we have already removed this silo
                    return;
                }

                if (CatalogSiloStatusListener != null)
                {
                    try
                    {
                        // only call SiloStatusChangeNotification once on the catalog and the order is important: call BEFORE updating membershipRingList.
                        CatalogSiloStatusListener.SiloStatusChangeNotification(silo, status);
                    }
                    catch (Exception exc)
                    {
                        log.Error(ErrorCode.Directory_SiloStatusChangeNotification_Exception,
                            String.Format("CatalogSiloStatusListener.SiloStatusChangeNotification has thrown an exception when notified about removed silo {0}.", silo.ToStringWithHashCode()), exc);
                    }
                }

                // the call order is important
                HandoffManager.ProcessSiloRemoveEvent(silo);
                membershipCache.Remove(silo);
                membershipRingList.Remove(silo);

                AdjustLocalDirectory(silo);
                AdjustLocalCache(silo);

                if (log.IsVerbose) log.Verbose("Silo {0} removed silo {1}", MyAddress, silo);
            }
        }

        /// <summary>
        /// Adjust local directory following the removal of a silo by droping all activations located on the removed silo
        /// </summary>
        /// <param name="removedSilo"></param>
        protected void AdjustLocalDirectory(SiloAddress removedSilo)
        {
            var activationsToRemove = (from pair in DirectoryPartition.GetItems()
                                       from pair2 in pair.Value.Instances.Where(pair3 => pair3.Value.SiloAddress.Equals(removedSilo))
                                       select new Tuple<GrainId, ActivationId>(pair.Key, pair2.Key)).ToList();
            // drop all records of activations located on the removed silo
            foreach (var activation in activationsToRemove)
            {
                DirectoryPartition.RemoveActivation(activation.Item1, activation.Item2);
            }
        }

        /// Adjust local cache following the removal of a silo by droping:
        /// 1) entries that point to activations located on the removed silo 
        /// 2) entries for grains that are now owned by this silo (me)
        /// 3) entries for grains that were owned by this removed silo - we currently do NOT do that.
        ///     If we did 3, we need to do that BEFORE we change the membershipRingList (based on old Membership).
        ///     We don't do that since first cache refresh handles that. 
        ///     Second, since Membership events are not guaranteed to be ordered, we may remove a cache entry that does not really point to a failed silo.
        ///     To do that properly, we need to store for each cache entry who was the directory owner that registered this activation (the original partition owner). 
        protected void AdjustLocalCache(SiloAddress removedSilo)
        {
            // remove all records of activations located on the removed silo
            foreach (Tuple<GrainId, IReadOnlyList<Tuple<SiloAddress, ActivationId>>, int> tuple in DirectoryCache.KeyValues)
            {
                // 2) remove entries now owned by me (they should be retrieved from my directory partition)
                if (MyAddress.Equals(CalculateTargetSilo(tuple.Item1)))
                {
                    DirectoryCache.Remove(tuple.Item1);
                }

                // 1) remove entries that point to activations located on the removed silo
                RemoveActivations(DirectoryCache, tuple.Item1, tuple.Item2, tuple.Item3, t => t.Item1.Equals(removedSilo));
            }
        }

        internal List<SiloAddress> FindPredecessors(SiloAddress silo, int count)
        {
            lock (membershipCache)
            {
                int index = membershipRingList.FindIndex(elem => elem.Equals(silo));
                if (index == -1)
                {
                    log.Warn(ErrorCode.Runtime_Error_100201, "Got request to find predecessors of silo " + silo + ", which is not in the list of members");
                    return null;
                }

                var result = new List<SiloAddress>();
                int numMembers = membershipRingList.Count;
                for (int i = index - 1; ((i + numMembers) % numMembers) != index && result.Count < count; i--)
                {
                    result.Add(membershipRingList[(i + numMembers) % numMembers]);
                }

                return result;
            }
        }

        internal List<SiloAddress> FindSuccessors(SiloAddress silo, int count)
        {
            lock (membershipCache)
            {
                int index = membershipRingList.FindIndex(elem => elem.Equals(silo));
                if (index == -1)
                {
                    log.Warn(ErrorCode.Runtime_Error_100203, "Got request to find successors of silo " + silo + ", which is not in the list of members");
                    return null;
                }

                var result = new List<SiloAddress>();
                int numMembers = membershipRingList.Count;
                for (int i = index + 1; i % numMembers != index && result.Count < count; i++)
                {
                    result.Add(membershipRingList[i % numMembers]);
                }

                return result;
            }
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            // This silo's status has changed
            if (Equals(updatedSilo, MyAddress))
            {
                if (status == SiloStatus.Stopping || status == SiloStatus.ShuttingDown)
                {
                    // QueueAction up the "Stop" to run on a system turn
                    Scheduler.QueueAction(() => Stop(true), CacheValidator.SchedulingContext).Ignore();
                }
                else if (status == SiloStatus.Dead)
                {
                    // QueueAction up the "Stop" to run on a system turn
                    Scheduler.QueueAction(() => Stop(false), CacheValidator.SchedulingContext).Ignore();
                }
            }
            else // Status change for some other silo
            {
                if (status.IsTerminating())
                {
                    // QueueAction up the "Remove" to run on a system turn
                    Scheduler.QueueAction(() => RemoveServer(updatedSilo, status), CacheValidator.SchedulingContext).Ignore();
                }
                else if (status == SiloStatus.Active)      // do not do anything with SiloStatus.Starting -- wait until it actually becomes active
                {
                    // QueueAction up the "Remove" to run on a system turn
                    Scheduler.QueueAction(() => AddServer(updatedSilo), CacheValidator.SchedulingContext).Ignore();
                }
            }
        }

        private bool IsValidSilo(SiloAddress silo)
        {
            if (Membership == null)
                Membership = Silo.CurrentSilo.LocalSiloStatusOracle;

            return Membership.IsFunctionalDirectory(silo);
        }

        #endregion

        /// <summary>
        /// Finds the silo that owns the directory information for the given grain ID.
        /// This routine will always return a non-null silo address unless the excludeThisSiloIfStopping parameter is true,
        /// this is the only silo known, and this silo is stopping.
        /// </summary>
        /// <param name="grainId"></param>
        /// <param name="excludeThisSiloIfStopping"></param>
        /// <returns></returns>
        public SiloAddress CalculateTargetSilo(GrainId grainId, bool excludeThisSiloIfStopping = true)
        {
            // give a special treatment for special grains
            if (grainId.IsSystemTarget)
            {
                if (log.IsVerbose2) log.Verbose2("Silo {0} looked for a system target {1}, returned {2}", MyAddress, grainId, MyAddress);
                // every silo owns its system targets
                return MyAddress;
            }

            if (Constants.SystemMembershipTableId.Equals(grainId))
            {
                if (Seed == null)
                {
                    string grainName;
                    if (!Constants.TryGetSystemGrainName(grainId, out grainName))
                        grainName = "MembershipTableGrain";
                    
                    var errorMsg = grainName + " cannot run without Seed node - please check your silo configuration file and make sure it specifies a SeedNode element. " +
                        " Alternatively, you may want to use AzureTable for LivenessType.";
                    throw new ArgumentException(errorMsg, "grainId = " + grainId);
                }
                // Directory info for the membership table grain has to be located on the primary (seed) node, for bootstrapping
                if (log.IsVerbose2) log.Verbose2("Silo {0} looked for a special grain {1}, returned {2}", MyAddress, grainId, Seed);
                return Seed;
            }

            SiloAddress siloAddress;
            int hash = unchecked((int)grainId.GetUniformHashCode());

            lock (membershipCache)
            {
                if (membershipRingList.Count == 0)
                {
                    // If the membership ring is empty, then we're the owner by default unless we're stopping.
                    return excludeThisSiloIfStopping && !Running ? null : MyAddress;
                }

                // excludeMySelf from being a TargetSilo if we're not running and the excludeThisSIloIfStopping flag is true. see the comment in the Stop method.
                bool excludeMySelf = !Running && excludeThisSiloIfStopping; 

                // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes
                siloAddress = membershipRingList.FindLast(siloAddr => (siloAddr.GetConsistentHashCode() <= hash) &&
                                    (!siloAddr.Equals(MyAddress) || !excludeMySelf));
                if (siloAddress == null)
                {
                    // If not found in the traversal, last silo will do (we are on a ring).
                    // We checked above to make sure that the list isn't empty, so this should always be safe.
                    siloAddress = membershipRingList[membershipRingList.Count - 1];
                    // Make sure it's not us...
                    if (siloAddress.Equals(MyAddress) && excludeMySelf)
                    {
                        siloAddress = membershipRingList.Count > 1 ? membershipRingList[membershipRingList.Count - 2] : null;
                    }
                }
            }
            if (log.IsVerbose2) log.Verbose2("Silo {0} calculated directory partition owner silo {1} for grain {2}: {3} --> {4}", MyAddress, siloAddress, grainId, hash, siloAddress.GetConsistentHashCode());
            return siloAddress;
        }

        #region Implementation of ILocalGrainDirectory

        public SiloAddress CheckIfShouldForward(GrainId grainId, int hopCount, string operationDescription)
        {
            SiloAddress owner = CalculateTargetSilo(grainId);

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
                var registrar = RegistrarManager.Instance.GetRegistrarForGrain(address.Grain);

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
            log.Verbose2("UnregisterAfterNonexistingActivation addr={0} origin={1}", addr, origin);

            if (origin == null || membershipCache.Contains(origin))
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

                var registrar = RegistrarManager.Instance.GetRegistrarForGrain(address.Grain);

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
                    var registrar = RegistrarManager.Instance.GetRegistrarForGrain(address.Grain);

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

            SiloAddress silo = CalculateTargetSilo(grain, false);
            // No need to check that silo != null since we're passing excludeThisSiloIfStopping = false

            if (log.IsVerbose) log.Verbose("Silo {0} tries to lookup for {1}-->{2} ({3}-->{4})", MyAddress, grain, silo, grain.GetUniformHashCode(), silo.GetConsistentHashCode());

            // check if we own the grain
            if (silo.Equals(MyAddress))
            {
                LocalDirectoryLookups.Increment();
                result = GetLocalDirectoryData(grain);
                if (result.Addresses == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were 
                    // some recent changes in the membership
                    if (log.IsVerbose2) log.Verbose2("LocalLookup mine {0}=null", grain);
                    return false;
                }
                if (log.IsVerbose2) log.Verbose2("LocalLookup mine {0}={1}", grain, result.Addresses.ToStrings());
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
                if (log.IsVerbose2) log.Verbose2("TryFullLookup else {0}=null", grain);
                return false;
            }
            if (log.IsVerbose2) log.Verbose2("LocalLookup cache {0}={1}", grain, result.Addresses.ToStrings());
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
            if (clusterId == null)
                throw new ArgumentNullException("clusterId");

            if (clusterId == ClusterId)
            {
                return LookupAsync(grainId);
            }
            else
            {
                // find gateway
                var gossipOracle = Silo.CurrentSilo.LocalMultiClusterOracle;
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
                    if (log.IsVerbose2) log.Verbose2("FullLookup mine {0}=none", grainId);
                    localResult.Addresses = new List<ActivationAddress>();
                    localResult.VersionTag = GrainInfo.NO_ETAG;
                    return localResult;
                }

                if (log.IsVerbose2) log.Verbose2("FullLookup mine {0}={1}", grainId, localResult.Addresses.ToStrings());
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
                if (log.IsVerbose2) log.Verbose2("FullLookup remote {0}={1}", grainId, result.Addresses.ToStrings());

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
                var registrar = RegistrarManager.Instance.GetRegistrarForGrain(grainId);

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
            if (invalidateDirectoryAlso && CalculateTargetSilo(grainId).Equals(MyAddress))
            {
                var registrar = RegistrarManager.Instance.GetRegistrarForGrain(grainId);
                registrar.InvalidateCache(activationAddress);
            }
        }

        /// <summary>
        /// For testing purposes only.
        /// Returns the silo that this silo thinks is the primary owner of directory information for
        /// the provided grain ID.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        public SiloAddress GetPrimaryForGrain(GrainId grain)
        {
            return CalculateTargetSilo(grain);
        }

        /// <summary>
        /// For testing purposes only.
        /// Returns the silos that this silo thinks hold copies of the directory information for
        /// the provided grain ID.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        public List<SiloAddress> GetSilosHoldingDirectoryInformationForGrain(GrainId grain)
        {
            var primary = CalculateTargetSilo(grain);
            return FindPredecessors(primary, 1);
        }

        /// <summary>
        /// For testing purposes only.
        /// Returns the directory information held by the local silo for the provided grain ID.
        /// The result will be null if no information is held.
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="isPrimary"></param>
        /// <returns></returns>
        public List<ActivationAddress> GetLocalDataForGrain(GrainId grain, out bool isPrimary)
        {
            var primary = CalculateTargetSilo(grain);
            List<ActivationAddress> backupData = HandoffManager.GetHandedOffInfo(grain);
            if (MyAddress.Equals(primary))
            {
                log.Assert(ErrorCode.DirectoryBothPrimaryAndBackupForGrain, backupData == null,
                    "Silo contains both primary and backup directory data for grain " + grain);
                isPrimary = true;
                return GetLocalDirectoryData(grain).Addresses;
            }

            isPrimary = false;
            return backupData;
        }

        #endregion

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
            if (localLookupsCurrent > 0)
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
            long distance;
            List<SiloAddress> successorList = FindSuccessors(MyAddress, 1);
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
            long distance;
            List<SiloAddress> successorList = FindSuccessors(MyAddress, 1);
            if (successorList == null || successorList.Count == 0)
            {
                distance = 0;
            }
            else
            {
                SiloAddress successor = successorList.First();
                distance = successor == null ? 0 : CalcRingDistance(MyAddress, successor);
            }
            double averageRingSpace = membershipRingList.Count == 0 ? 0 : (1.0 / (double)membershipRingList.Count);
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
            var sb = new StringBuilder();

            sb.AppendFormat("Silo address is {0}, silo consistent hash is {1:X}.", MyAddress, MyAddress.GetConsistentHashCode()).AppendLine();
            sb.AppendLine("Ring is:");
            lock (membershipCache)
            {
                foreach (var silo in membershipRingList)
                    sb.AppendFormat("    Silo {0}, consistent hash is {1:X}", silo, silo.GetConsistentHashCode()).AppendLine();
            }

            sb.AppendFormat("My predecessors: {0}", FindPredecessors(MyAddress, 1).ToStrings(addr => String.Format("{0}/{1:X}---", addr, addr.GetConsistentHashCode()), " -- ")).AppendLine();
            sb.AppendFormat("My successors: {0}", FindSuccessors(MyAddress, 1).ToStrings(addr => String.Format("{0}/{1:X}---", addr, addr.GetConsistentHashCode()), " -- "));
            return sb.ToString();
        }

        internal IRemoteGrainDirectory GetDirectoryReference(SiloAddress silo)
        {
            return InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceId, silo);
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
            lock (membershipCache)
            {
                return membershipCache.Contains(silo);
            }
        }

    }
}
