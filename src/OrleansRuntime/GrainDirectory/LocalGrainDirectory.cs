/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory
{
    internal class LocalGrainDirectory : MarshalByRefObject, ILocalGrainDirectory, ISiloStatusListener
    {
        /// <summary>
        /// list of silo members sorted by the hash value of their address
        /// </summary>
        private readonly List<SiloAddress> membershipRingList;
        private readonly HashSet<SiloAddress> membershipCache;
        private readonly AsynchAgent maintainer;
        private readonly TraceLogger log;
        private readonly SiloAddress seed;
        internal ISiloStatusOracle Membership;

        // Consider: move these constants into an apropriate place
        // number of retries to redirect a wrong request (which can get wrong beacuse of membership changes)
        internal const int NUM_RETRIES = 3;

        protected SiloAddress Seed { get { return seed; } }

        internal bool Running;

        internal SiloAddress MyAddress { get; private set; }

        internal IGrainDirectoryCache<List<Tuple<SiloAddress, ActivationId>>> DirectoryCache { get; private set; }
        internal GrainDirectoryPartition DirectoryPartition { get; private set; }

        public RemoteGrainDirectory RemGrainDirectory { get; private set; }
        public RemoteGrainDirectory CacheValidator { get; private set; }

        private readonly TaskCompletionSource<bool> stopPreparationResolver;
        public Task StopPreparationCompletion { get { return stopPreparationResolver.Task; } }

        internal OrleansTaskScheduler Scheduler { get; private set; }

        internal GrainDirectoryHandoffManager HandoffManager { get; private set; }

        internal ISiloStatusListener CatalogSiloStatusListener { get; set; }

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
            log = TraceLogger.GetLogger("Orleans.GrainDirectory.LocalGrainDirectory");

            MyAddress = silo.LocalMessageCenter.MyAddress;
            Scheduler = silo.LocalScheduler;
            membershipRingList = new List<SiloAddress>();
            membershipCache = new HashSet<SiloAddress>();

            silo.OrleansConfig.OnConfigChange("Globals/Caching", () =>
            {
                lock (membershipCache)
                {
                    DirectoryCache = GrainDirectoryCacheFactory<List<Tuple<SiloAddress, ActivationId>>>.CreateGrainDirectoryCache(silo.GlobalConfig);
                }
            });
            maintainer = GrainDirectoryCacheFactory<List<Tuple<SiloAddress, ActivationId>>>.CreateGrainDirectoryCacheMaintainer(this, DirectoryCache);

            if (silo.GlobalConfig.SeedNodes.Count > 0)
            {
                seed = silo.GlobalConfig.SeedNodes.Contains(MyAddress.Endpoint) ? MyAddress : SiloAddress.New(silo.GlobalConfig.SeedNodes[0], 0);
            }

            stopPreparationResolver = new TaskCompletionSource<bool>();
            DirectoryPartition = new GrainDirectoryPartition();
            HandoffManager = new GrainDirectoryHandoffManager(this, silo.GlobalConfig);

            RemGrainDirectory = new RemoteGrainDirectory(this, Constants.DirectoryServiceId);
            CacheValidator = new RemoteGrainDirectory(this, Constants.DirectoryCacheValidatorId);

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
            Running = true;
            if (maintainer != null)
            {
                maintainer.Start();
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
                DirectoryPartition.RemoveActivation(activation.Item1, activation.Item2, true);
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
            foreach (Tuple<GrainId, List<Tuple<SiloAddress, ActivationId>>, int> tuple in DirectoryCache.KeyValues)
            {
                // 2) remove entries owned by me (they should be retrieved from my directory partition)
                if (MyAddress.Equals(CalculateTargetSilo(tuple.Item1)))
                {
                    DirectoryCache.Remove(tuple.Item1);
                }

                // 1) remove entries that point to activations located on the removed silo
                if (tuple.Item2.RemoveAll(tuple2 => tuple2.Item1.Equals(removedSilo)) <= 0) continue;

                if (tuple.Item2.Count > 0)
                {
                    DirectoryCache.AddOrUpdate(tuple.Item1, tuple.Item2, tuple.Item3);
                }
                else
                {
                    DirectoryCache.Remove(tuple.Item1);
                }
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
                if (status == SiloStatus.Stopping || status.Equals(SiloStatus.ShuttingDown))
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
                if (status.Equals(SiloStatus.Dead) || status.Equals(SiloStatus.ShuttingDown) || status.Equals(SiloStatus.Stopping))
                {
                    // QueueAction up the "Remove" to run on a system turn
                    Scheduler.QueueAction(() => RemoveServer(updatedSilo, status), CacheValidator.SchedulingContext).Ignore();
                }
                else if (status.Equals(SiloStatus.Active))      // do not do anything with SiloStatus.Starting -- wait until it actually becomes active
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
            
            return Membership.IsValidSilo(silo);
        }

        #endregion

        /// <summary>
        /// Finds the silo that owns the directory information for the given grain ID.
        /// This routine will always return a non-null silo address unless the excludeThisSiloIfStopping parameter is true,
        /// this is the only silo known, and this silo is stopping.
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="excludeThisSiloIfStopping"></param>
        /// <returns></returns>
        public SiloAddress CalculateTargetSilo(GrainId grain, bool excludeThisSiloIfStopping = true)
        {
            // give a special treatment for special grains
            if (grain.IsSystemTarget)
            {
                if (log.IsVerbose2) log.Verbose2("Silo {0} looked for a system target {1}, returned {2}", MyAddress, grain, MyAddress);
                // every silo owns its system targets
                return MyAddress;
            }

            if (Constants.SystemMembershipTableId.Equals(grain))
            {
                if (Seed == null)
                {
                    string grainName;
                    if (!Constants.TryGetSystemGrainName(grain, out grainName))
                        grainName = "MembershipTableGrain";
                    
                    var errorMsg = grainName + " cannot run without Seed node - please check your silo configuration file and make sure it specifies a SeedNode element. " +
                        " Alternatively, you may want to use AzureTable for LivenessType.";
                    throw new ArgumentException(errorMsg, "grain = " + grain);
                }
                // Directory info for the membership table grain has to be located on the primary (seed) node, for bootstrapping
                if (log.IsVerbose2) log.Verbose2("Silo {0} looked for a special grain {1}, returned {2}", MyAddress, grain, Seed);
                return Seed;
            }

            SiloAddress s;
            int hash = unchecked((int)grain.GetUniformHashCode());

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
                s = membershipRingList.FindLast(siloAddr => (siloAddr.GetConsistentHashCode() <= hash) &&
                                    (!siloAddr.Equals(MyAddress) || !excludeMySelf));
                if (s == null)
                {
                    // If not found in the traversal, last silo will do (we are on a ring).
                    // We checked above to make sure that the list isn't empty, so this should always be safe.
                    s = membershipRingList[membershipRingList.Count - 1];
                    // Make sure it's not us...
                    if (s.Equals(MyAddress) && excludeMySelf)
                    {
                        s = membershipRingList.Count > 1 ? membershipRingList[membershipRingList.Count - 2] : null;
                    }
                }
            }
            if (log.IsVerbose2) log.Verbose2("Silo {0} calculated directory partition owner silo {1} for grain {2}: {3} --> {4}", MyAddress, s, grain, hash, s.GetConsistentHashCode());
            return s;
        }

        #region Implementation of ILocalGrainDirectory

        /// <summary>
        /// Registers a new activation, in single activation mode, with the directory service.
        /// If there is already an activation registered for this grain, then the new activation will
        /// not be registered and the address of the existing activation will be returned.
        /// Otherwise, the passed-in address will be returned.
        /// <para>This method must be called from a scheduler thread.</para>
        /// </summary>
        /// <param name="address">The address of the potential new activation.</param>
        /// <returns>The address registered for the grain's single activation.</returns>
        public async Task<ActivationAddress> RegisterSingleActivationAsync(ActivationAddress address)
        {
            registrationsSingleActIssued.Increment();
            SiloAddress owner = CalculateTargetSilo(address.Grain);
            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }
            if (owner.Equals(MyAddress))
            {
                RegistrationsSingleActLocal.Increment();
                // if I am the owner, store the new activation locally
                Tuple<ActivationAddress, int> returnedAddress = DirectoryPartition.AddSingleActivation(address.Grain, address.Activation, address.Silo);
                return returnedAddress == null ? null : returnedAddress.Item1;
            }
            else
            {
                RegistrationsSingleActRemoteSent.Increment();
                // otherwise, notify the owner
                Tuple<ActivationAddress, int> returnedAddress = await GetDirectoryReference(owner).RegisterSingleActivation(address, NUM_RETRIES);

                // Caching optimization: 
                // cache the result of a successfull RegisterSingleActivation call, only if it is not a duplicate activation.
                // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                if (returnedAddress == null || returnedAddress.Item1 == null) return null;

                if (!address.Equals(returnedAddress.Item1) || !IsValidSilo(address.Silo)) return returnedAddress.Item1;

                var cached = new List<Tuple<SiloAddress, ActivationId>>( new [] { Tuple.Create(address.Silo, address.Activation) });
                // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                DirectoryCache.AddOrUpdate(address.Grain, cached, returnedAddress.Item2);
                return returnedAddress.Item1;
            }
        }

        public async Task RegisterAsync(ActivationAddress address)
        {
            registrationsIssued.Increment();
            SiloAddress owner = CalculateTargetSilo(address.Grain);
            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }
            if (owner.Equals(MyAddress))
            {
                RegistrationsLocal.Increment();
                // if I am the owner, store the new activation locally
                DirectoryPartition.AddActivation(address.Grain, address.Activation, address.Silo);
            }
            else
            {
                RegistrationsRemoteSent.Increment();
                // otherwise, notify the owner
                int eTag = await GetDirectoryReference(owner).Register(address, NUM_RETRIES);
                if (IsValidSilo(address.Silo))
                {
                    // Caching optimization:
                    // cache the result of a successfull RegisterActivation call, only if it is not a duplicate activation.
                    // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                    List<Tuple<SiloAddress, ActivationId>> cached;
                    if (!DirectoryCache.LookUp(address.Grain, out cached))
                    {
                        cached = new List<Tuple<SiloAddress, ActivationId>>(1);
                    }
                    cached.Add(Tuple.Create(address.Silo, address.Activation));
                    // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                    DirectoryCache.AddOrUpdate(address.Grain, cached, eTag);
                }
            }
        }

        public Task UnregisterAsync(ActivationAddress addr)
        {
            return UnregisterAsyncImpl(addr, true);
        }

        public Task UnregisterConditionallyAsync(ActivationAddress addr)
        {
            // This is a no-op if the lazy registration delay is zero or negative
            return Silo.CurrentSilo.OrleansConfig.Globals.DirectoryLazyDeregistrationDelay <= TimeSpan.Zero ? 
                TaskDone.Done : UnregisterAsyncImpl(addr, false);
        }

        private Task UnregisterAsyncImpl(ActivationAddress addr, bool force)
        {
            unregistrationsIssued.Increment();
            SiloAddress owner = CalculateTargetSilo(addr.Grain);
            if (owner == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }

            if (log.IsVerbose) log.Verbose("Silo {0} is going to unregister grain {1}-->{2} ({3}-->{4})", MyAddress, addr.Grain, owner, addr.Grain.GetUniformHashCode(), owner.GetConsistentHashCode());

            InvalidateCacheEntry(addr);
            if (owner.Equals(MyAddress))
            {
                UnregistrationsLocal.Increment();
                // if I am the owner, remove the old activation locally
                DirectoryPartition.RemoveActivation(addr.Grain, addr.Activation, force);
                return TaskDone.Done;
            }
            
            UnregistrationsRemoteSent.Increment();
            // otherwise, notify the owner
            return GetDirectoryReference(owner).Unregister(addr, force, NUM_RETRIES);
        }

        public Task UnregisterManyAsync(List<ActivationAddress> addresses)
        {
            unregistrationsManyIssued.Increment();
            return Task.WhenAll(
                addresses.GroupBy(a => CalculateTargetSilo(a.Grain))
                    .Select(g =>
                    {
                        if (g.Key == null)
                        {
                            // We don't know about any other silos, and we're stopping, so throw
                            throw new InvalidOperationException("Grain directory is stopping");
                        }

                        foreach (var addr in g)
                            InvalidateCacheEntry(addr);
                        
                        if (MyAddress.Equals(g.Key))
                        {
                            // if I am the owner, remove the old activation locally
                            foreach (var addr in g)
                            {
                                UnregistrationsLocal.Increment();
                                DirectoryPartition.RemoveActivation(addr.Grain, addr.Activation, true);
                            }
                            return TaskDone.Done;
                        }
                        
                        UnregistrationsManyRemoteSent.Increment();
                        // otherwise, notify the owner
                        return GetDirectoryReference(g.Key).UnregisterMany(g.ToList(), NUM_RETRIES);
                    }));
        }

        public bool LocalLookup(GrainId grain, out List<ActivationAddress> addresses)
        {
            localLookups.Increment();

            SiloAddress silo = CalculateTargetSilo(grain, false);
            // No need to check that silo != null since we're passing excludeThisSiloIfStopping = false

            if (log.IsVerbose) log.Verbose("Silo {0} tries to lookup for {1}-->{2} ({3}-->{4})", MyAddress, grain, silo, grain.GetUniformHashCode(), silo.GetConsistentHashCode());

            // check if we own the grain
            if (silo.Equals(MyAddress))
            {
                LocalDirectoryLookups.Increment();
                addresses = GetLocalDirectoryData(grain);
                if (addresses == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were 
                    // some recent changes in the membership
                    if (log.IsVerbose2) log.Verbose2("LocalLookup mine {0}=null", grain);
                    return false;
                }
                if (log.IsVerbose2) log.Verbose2("LocalLookup mine {0}={1}", grain, addresses.ToStrings());
                LocalDirectorySuccesses.Increment();
                localSuccesses.Increment();
                return true;
            }

            // handle cache
            cacheLookups.Increment();
            addresses = GetLocalCacheData(grain);
            if (addresses == null)
            {
                if (log.IsVerbose2) log.Verbose2("TryFullLookup else {0}=null", grain);
                return false;
            }
            if (log.IsVerbose2) log.Verbose2("LocalLookup cache {0}={1}", grain, addresses.ToStrings());
            cacheSuccesses.Increment();
            localSuccesses.Increment();
            return true;
        }

        public List<ActivationAddress> GetLocalDirectoryData(GrainId grain)
        {
            var result = DirectoryPartition.LookUpGrain(grain);
            return result == null ? null : 
                result.Item1.Select(t => ActivationAddress.GetAddress(t.Item1, grain, t.Item2)).Where(addr => IsValidSilo(addr.Silo)).ToList();
        }

        public List<ActivationAddress> GetLocalCacheData(GrainId grain)
        {
            List<Tuple<SiloAddress, ActivationId>> cached;
            return DirectoryCache.LookUp(grain, out cached) ? 
                cached.Select(elem => ActivationAddress.GetAddress(elem.Item1, grain, elem.Item2)).Where(addr => IsValidSilo(addr.Silo)).ToList() : 
                null;
        }

        public async Task<List<ActivationAddress>> FullLookup(GrainId grain)
        {
            fullLookups.Increment();

            SiloAddress silo = CalculateTargetSilo(grain, false);
            // No need to check that silo != null since we're passing excludeThisSiloIfStopping = false

            if (log.IsVerbose) log.Verbose("Silo {0} fully lookups for {1}-->{2} ({3}-->{4})", MyAddress, grain, silo, grain.GetUniformHashCode(), silo.GetConsistentHashCode());

            // We assyme that getting here means the grain was not found locally (i.e., in TryFullLookup()).
            // We still check if we own the grain locally to avoid races between the time TryFullLookup() and FullLookup() were called.
            if (silo.Equals(MyAddress))
            {
                LocalDirectoryLookups.Increment();
                var localResult = DirectoryPartition.LookUpGrain(grain);
                if (localResult == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were 
                    // some recent changes in the membership
                    if (log.IsVerbose2) log.Verbose2("FullLookup mine {0}=none", grain);
                    return new List<ActivationAddress>();
                }
                var a = localResult.Item1.Select(t => ActivationAddress.GetAddress(t.Item1, grain, t.Item2)).Where(addr => IsValidSilo(addr.Silo)).ToList();
                if (log.IsVerbose2) log.Verbose2("FullLookup mine {0}={1}", grain, a.ToStrings());
                LocalDirectorySuccesses.Increment();
                return a;
            }

            // Just a optimization. Why sending a message to someone we know is not valid.
            if (!IsValidSilo(silo))
            {
                throw new OrleansException(String.Format("Current directory at {0} is not stable to perform the lookup for grain {1} (it maps to {2}, which is not a valid silo). Retry later.", MyAddress, grain, silo));
            }

            RemoteLookupsSent.Increment();
            Tuple<List<Tuple<SiloAddress, ActivationId>>, int> result = await GetDirectoryReference(silo).LookUp(grain, NUM_RETRIES);

            // update the cache
            List<Tuple<SiloAddress, ActivationId>> entries = result.Item1.Where(t => IsValidSilo(t.Item1)).ToList();
            List<ActivationAddress> addresses = entries.Select(t => ActivationAddress.GetAddress(t.Item1, grain, t.Item2)).ToList();
            if (log.IsVerbose2) log.Verbose2("FullLookup remote {0}={1}", grain, addresses.ToStrings());

            if (entries.Count > 0)
                DirectoryCache.AddOrUpdate(grain, entries, result.Item2);
            
            return addresses;
        }

        public Task DeleteGrain(GrainId grain)
        {
            SiloAddress silo = CalculateTargetSilo(grain);
            if (silo == null)
            {
                // We don't know about any other silos, and we're stopping, so throw
                throw new InvalidOperationException("Grain directory is stopping");
            }

            if (log.IsVerbose) log.Verbose("Silo {0} tries to lookup for {1}-->{2} ({3}-->{4})", MyAddress, grain, silo, grain.GetUniformHashCode(), silo.GetConsistentHashCode());

            if (silo.Equals(MyAddress))
            {
                // remove from our partition
                DirectoryPartition.RemoveGrain(grain);
                return TaskDone.Done;
            }

            // remove from the cache
            DirectoryCache.Remove(grain);

            // send request to the owner
            return GetDirectoryReference(silo).DeleteGrain(grain, NUM_RETRIES);
        }

        public void InvalidateCacheEntry(ActivationAddress activationAddress)
        {
            var grainId = activationAddress.Grain;
            var activationId = activationAddress.Activation;
            List<Tuple<SiloAddress, ActivationId>> list;
            if (!DirectoryCache.LookUp(grainId, out list)) return;

            list.RemoveAll(tuple => tuple.Item2.Equals(activationId));

            // if list empty, need to remove from cache
            if (list.Count == 0)
                DirectoryCache.Remove(grainId);
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
                return GetLocalDirectoryData(grain);
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
            return RemoteGrainDirectoryFactory.GetSystemTarget(Constants.DirectoryServiceId, silo);
        }
    }
}
