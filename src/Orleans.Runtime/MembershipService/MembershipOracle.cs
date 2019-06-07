using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime.MembershipService
{
    internal partial class MembershipTableManager : SystemTarget, IMembershipService, IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly static TimeSpan shutdownGossipTimeout = TimeSpan.FromMilliseconds(3000);
        private readonly IInternalGrainFactory grainFactory;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IMembershipTable membershipTableProvider;

        private readonly ILogger log;
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly DateTime siloStartTime = DateTime.UtcNow;
        private SiloAddress MyAddress => this.localSiloDetails.SiloAddress;
        private GrainTimer timerGetTableUpdates;

        private const int NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS = -1; // unlimited
        private const int NUM_CONDITIONAL_WRITE_ERROR_ATTEMPTS = -1;
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MIN = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan EXP_BACKOFF_CONTENTION_MIN = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan EXP_BACKOFF_ERROR_MAX;
        private readonly TimeSpan EXP_BACKOFF_CONTENTION_MAX; // set based on config
        private static readonly TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromMilliseconds(1000);
        private readonly ILogger timerLogger;

        private SiloStatus CurrentStatus { get; set; }  // current status of this silo.
        private readonly ChangeFeedSource<MembershipTableSnapshot> membershipTableUpdates;
        private MembershipTableSnapshot membershipTableSnapshot;

        public MembershipTableSnapshot MembershipTableSnapshot => this.membershipTableSnapshot;

        public ChangeFeedEntry<MembershipTableSnapshot> MembershipTableUpdates => this.membershipTableUpdates.Current;

        private readonly ILoggerFactory loggerFactory;
        private MembershipTableData tableCache;

        public MembershipTableManager(
            ILocalSiloDetails localSiloDetails,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IMembershipTable membershipTable,
            IInternalGrainFactory grainFactory,
            ILogger<MembershipTableManager> log,
            ILoggerFactory loggerFactory)
            : base(Constants.MembershipOracleId, localSiloDetails.SiloAddress, loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            this.localSiloDetails = localSiloDetails;
            this.membershipTableProvider = membershipTable;
            this.grainFactory = grainFactory;
            this.clusterMembershipOptions = clusterMembershipOptions.Value;
            this.log = log;
            
            var backOffMax = StandardExtensions.Max(EXP_BACKOFF_STEP.Multiply(this.clusterMembershipOptions.ExpectedClusterSize), SiloMessageSender.CONNECTION_RETRY_DELAY.Multiply(2));
            EXP_BACKOFF_CONTENTION_MAX = backOffMax;
            EXP_BACKOFF_ERROR_MAX = backOffMax;
            timerLogger = this.loggerFactory.CreateLogger<GrainTimer>();

            this.membershipTableSnapshot = new MembershipTableSnapshot(localSiloDetails, MembershipVersion.MinValue, ImmutableDictionary<SiloAddress, MembershipEntry>.Empty);
            this.membershipTableUpdates = new ChangeFeedSource<MembershipTableSnapshot>(
                (previous, proposed) => proposed.Version > previous.Version,
                this.membershipTableSnapshot);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            {
                lifecycle.Subscribe(nameof(MembershipTableManager), ServiceLifecycleStage.RuntimeGrainServices, OnRuntimeGrainServicesStart, OnRuntimeGrainServicesStop);

                async Task OnRuntimeGrainServicesStart(CancellationToken ct)
                {
                    await this.Start();
                }

                Task OnRuntimeGrainServicesStop(CancellationToken ct)
                {
                    return Task.CompletedTask;
                }
            }

            {
                lifecycle.Subscribe(nameof(MembershipTableManager), ServiceLifecycleStage.BecomeActive, OnBecomeActiveStart, OnBecomeActiveStop);

                Task OnBecomeActiveStart(CancellationToken ct)
                {
                    this.BecomeActive();
                    return Task.CompletedTask;
                }

                Task OnBecomeActiveStop(CancellationToken ct)
                {
                    return Task.CompletedTask;
                }
            }
        }

        public async Task Start()
        {
            try
            {
                log.Info(ErrorCode.MembershipStarting, "MembershipOracle starting on host = " + this.localSiloDetails.DnsHostName + " address = " + MyAddress + " at " + LogFormatter.PrintDate(this.siloStartTime) + ", backOffMax = " + EXP_BACKOFF_CONTENTION_MAX);

                // Init the membership table.
                await this.membershipTableProvider.InitializeMembershipTable(true);

                if (this.clusterMembershipOptions.ExpectedClusterSize > 1)
                {
                    // randomly delay the startup, so not all silos write to the table at once.
                    // Use random time not larger than MaxJoinAttemptTime, one minute and 0.5sec*ExpectedClusterSize;
                    // Skip waiting if we expect only one member for the cluster.
                    var random = new SafeRandom();
                    var maxDelay = TimeSpan.FromMilliseconds(500).Multiply(this.clusterMembershipOptions.ExpectedClusterSize);
                    maxDelay = StandardExtensions.Min(maxDelay, StandardExtensions.Min(this.clusterMembershipOptions.MaxJoinAttemptTime, TimeSpan.FromMinutes(1)));
                    var randomDelay = random.NextTimeSpan(maxDelay);
                    await Task.Delay(randomDelay);
                }
                
                MembershipTableData table = await membershipTableProvider.ReadAll();
                if (log.IsEnabled(LogLevel.Debug)) log.Debug("-ReadAll Membership table {0}", table.ToString());
                LogMissedIAmAlives(table);
                await this.ProcessTableUpdate(table, nameof(Start));

                // read the table and look for my node migration occurrences
                DetectNodeMigration(table, this.localSiloDetails.DnsHostName);
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.MembershipFailedToStart, "MembershipFailedToStart", exc);
                throw;
            }
        }

        internal async Task UpdateIAmAlive()
        {
            var entry = new MembershipEntry
            {
                SiloAddress = MyAddress,
                IAmAliveTime = DateTime.UtcNow
            };

            await this.membershipTableProvider.UpdateIAmAlive(entry);
        }

        private void DetectNodeMigration(MembershipTableData table, string myHostname)
        {
            string mySiloName = this.localSiloDetails.Name;
            MembershipEntry mostRecentPreviousEntry = null;
            // look for silo instances that are same as me, find most recent with Generation before me.
            foreach (MembershipEntry entry in table.Members.Select(tuple => tuple.Item1).Where(data => mySiloName.Equals(data.SiloName)))
            {
                bool iAmLater = MyAddress.Generation.CompareTo(entry.SiloAddress.Generation) > 0;
                // more recent
                if (iAmLater && (mostRecentPreviousEntry == null || entry.SiloAddress.Generation.CompareTo(mostRecentPreviousEntry.SiloAddress.Generation) > 0))
                    mostRecentPreviousEntry = entry;
            }

            if (mostRecentPreviousEntry != null)
            {
                bool physicalHostChanged = !myHostname.Equals(mostRecentPreviousEntry.HostName) || !MyAddress.Endpoint.Equals(mostRecentPreviousEntry.SiloAddress.Endpoint);
                if (physicalHostChanged)
                {
                    string error = string.Format("Silo {0} migrated from host {1} silo address {2} to host {3} silo address {4}.",
                        mySiloName, myHostname, MyAddress, mostRecentPreviousEntry.HostName, mostRecentPreviousEntry.SiloAddress);
                    log.Warn(ErrorCode.MembershipNodeMigrated, error);
                }
                else
                {
                    string error = string.Format("Silo {0} restarted on same host {1} New silo address = {2} Previous silo address = {3}",
                        mySiloName, myHostname, MyAddress, mostRecentPreviousEntry.SiloAddress);
                    log.Warn(ErrorCode.MembershipNodeRestarted, error);
                }
            }
        }

        private void BecomeActive()
        {
            log.Info(ErrorCode.MembershipBecomeActive, "-BecomeActive");

            // write myself to the table
            // read the table and store locally the list of live silos
            try
            {
                var random = new SafeRandom();
                var randomTableOffset = random.NextTimeSpan(this.clusterMembershipOptions.TableRefreshTimeout);
                if (timerGetTableUpdates != null)
                    timerGetTableUpdates.Dispose();
                timerGetTableUpdates = GrainTimer.FromTimerCallback(
                    this.RuntimeClient.Scheduler,
                    timerLogger,
                    OnGetTableUpdateTimer,
                    null,
                    randomTableOffset,
                    this.clusterMembershipOptions.TableRefreshTimeout,
                    "Membership.ReadTableTimer");

                log.Info(ErrorCode.MembershipFinishBecomeActive, "-Finished BecomeActive.");
            }
            catch (Exception exc)
            {
                log.Error(ErrorCode.MembershipFailedToBecomeActive, "BecomeActive failed", exc);
                throw;
            }
        }

        private bool IsFunctionalForMembership(SiloStatus status)
        {
            return status == SiloStatus.Active || status == SiloStatus.ShuttingDown || status == SiloStatus.Stopping;
        }

        // Treat this gossip msg as a trigger to read the table (and just ignore the input parameters).
        // This simplified a lot of the races when we get gossip info which is outdated with the table truth.
        public async Task SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (log.IsEnabled(LogLevel.Trace)) log.Trace("-Received GOSSIP SiloStatusChangeNotification about {0} status {1}. Going to read the table.", updatedSilo, status);
            if (IsFunctionalForMembership(CurrentStatus))
            {
                try
                {
                    MembershipTableData table = await membershipTableProvider.ReadAll();
                    await ProcessTableUpdate(table, "gossip");
                }
                catch (Exception exc)
                {
                    log.Error(ErrorCode.MembershipGossipProcessingFailure, 
                        "Error doing SiloStatusChangeNotification", exc);
                    throw;
                }
            }
        }

        public Task Ping(int pingNumber)
        {
            // do not do anything here -- simply returning back will indirectly notify the prober that this silo is alive
            return Task.CompletedTask;
        }

        private Task<bool> MembershipExecuteWithRetries(
            Func<int, Task<bool>> taskFunction, 
            TimeSpan timeout)
        {
            return AsyncExecutorWithRetries.ExecuteWithRetries(
                    taskFunction,
                    NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS,
                    NUM_CONDITIONAL_WRITE_ERROR_ATTEMPTS,
                    (result, i) => result == false,   // if failed to Update on contention - retry   
                    (exc, i) => true,            // Retry on errors.          
                    timeout,
                    new ExponentialBackoff(EXP_BACKOFF_CONTENTION_MIN, this.EXP_BACKOFF_CONTENTION_MAX, EXP_BACKOFF_STEP), // how long to wait between successful retries
                    new ExponentialBackoff(EXP_BACKOFF_ERROR_MIN, this.EXP_BACKOFF_ERROR_MAX, EXP_BACKOFF_STEP)  // how long to wait between error retries
            );
        }

        private Task CleanupTable()
        {
            Func<int, Task<bool>> cleanupTableEntriesTask = async counter => 
            {
                if (log.IsEnabled(LogLevel.Debug)) log.Debug("-Attempting CleanupTableEntries #{0}", counter);
                MembershipTableData table = await membershipTableProvider.ReadAll();
                log.Info(ErrorCode.MembershipReadAll_Cleanup, "-CleanupTable called on silo startup. Membership table {0}",
                    table.ToString());

                return await CleanupMyTableEntries(table);
            };

            return MembershipExecuteWithRetries(cleanupTableEntriesTask, this.clusterMembershipOptions.MaxJoinAttemptTime);
        }

        public async Task UpdateMyStatusGlobal(SiloStatus status)
        {
            if (status == SiloStatus.Joining)
            {
                // first, cleanup all outdated entries of myself from the table
                await CleanupTable();
            }

            if (status == SiloStatus.Dead && this.membershipTableProvider is SystemTargetBasedMembershipTable)
            {
                this.CurrentStatus = status;

                // SystemTarget-based clustering does not support transitioning to Dead locally since at this point app scheduler turns have been stopped.
                return;
            }

            string errorString = null;
            int numCalls = 0;
            
            try
            {
                Func<int, Task<bool>> updateMyStatusTask = async counter =>
                {
                    numCalls++;
                    if (log.IsEnabled(LogLevel.Debug)) log.Debug("-Going to try to TryUpdateMyStatusGlobalOnce #{0}", counter);
                    return await TryUpdateMyStatusGlobalOnce(status);  // function to retry
                };

                bool ok = await MembershipExecuteWithRetries(updateMyStatusTask, this.clusterMembershipOptions.MaxJoinAttemptTime);

                if (ok)
                {
                    if (log.IsEnabled(LogLevel.Debug)) log.Debug("-Silo {0} Successfully updated my Status in the Membership table to {1}", MyAddress, status);
                    if (status == SiloStatus.Stopping || status == SiloStatus.ShuttingDown || status == SiloStatus.Dead)
                    {
                        try
                        {
                            await GossipMyStatus().WithTimeout(shutdownGossipTimeout);
                        }
                        catch (Exception e)
                        {
                            this.log.LogWarning($"GossipMyStatus failed when silo {status}, due to exception {e}");
                        }
                    }
                    else
                    {
                        GossipMyStatus().Ignore();
                    }
                    
                }
                else
                {
                    errorString = $"-Silo {MyAddress} failed to update its status to {status} in the Membership table due to write contention on the table after {numCalls} attempts.";
                    log.Error(ErrorCode.MembershipFailedToWriteConditional, errorString);
                    throw new OrleansException(errorString);
                }
            }
            catch (Exception exc)
            {
                if (errorString == null)
                {
                    errorString = $"-Silo {this.MyAddress} failed to update its status to {status} in the table due to failures (socket failures or table read/write failures) after {numCalls} attempts: {exc.Message}";
                    log.Error(ErrorCode.MembershipFailedToWrite, errorString);
                    throw new OrleansException(errorString, exc);
                }
                throw;
            }
        }

        // read the table
        // find all currently active nodes and test pings to all of them
        //      try to ping all
        //      if all pings succeeded
        //             try to change my status to Active and in the same write transaction update Membership version row, conditioned on both etags
        //      if failed (on ping or on write exception or on etag) - retry the whole AttemptToJoinActiveNodes
        private async Task<bool> TryUpdateMyStatusGlobalOnce(SiloStatus newStatus)
        {
            var table = await membershipTableProvider.ReadAll();

            if (log.IsEnabled(LogLevel.Debug)) log.Debug("-TryUpdateMyStatusGlobalOnce: Read{0} Membership table {1}", (newStatus.Equals(SiloStatus.Active) ? "All" : " my entry from"), table.ToString());
            LogMissedIAmAlives(table);

            MembershipEntry myEntry;
            string myEtag = null;
            if (table.Contains(MyAddress))
            {
                var myTuple = table.Get(MyAddress);
                myEntry = myTuple.Item1;
                myEtag = myTuple.Item2;
                myEntry.TryUpdateStartTime(this.siloStartTime);
                if (myEntry.Status == SiloStatus.Dead) // check if the table already knows that I am dead
                {
                    var msg = string.Format("I should be Dead according to membership table (in TryUpdateMyStatusGlobalOnce): myEntry = {0}.", myEntry.ToFullString());
                    log.Warn(ErrorCode.MembershipFoundMyselfDead1, msg);
                    KillMyselfLocally(msg);
                    return true;
                }
            }
            else // first write attempt of this silo. Insert instead of Update.
            {
                var assy = Assembly.GetEntryAssembly() ?? typeof(MembershipTableCache).Assembly;
                var roleName = assy.GetName().Name;

                myEntry = new MembershipEntry
                {
                    SiloAddress = this.localSiloDetails.SiloAddress,

                    HostName = this.localSiloDetails.DnsHostName,
                    SiloName = this.localSiloDetails.Name,

                    Status = newStatus,
                    ProxyPort = this.localSiloDetails.GatewayAddress?.Endpoint?.Port ?? 0,

                    RoleName = roleName,

                    SuspectTimes = new List<Tuple<SiloAddress, DateTime>>(),
                    StartTime = this.siloStartTime,
                    IAmAliveTime = DateTime.UtcNow
                };
            }

            var now = DateTime.UtcNow;
            if (newStatus == SiloStatus.Dead)
                myEntry.AddSuspector(MyAddress, now); // add the killer (myself) to the suspect list, for easier diagnostics later on.
            
            myEntry.Status = newStatus;
            myEntry.IAmAliveTime = now;

            bool ok;
            TableVersion next = table.Version.Next();
            if (myEtag != null) // no previous etag for my entry -> its the first write to this entry, so insert instead of update.
            {
                ok = await membershipTableProvider.UpdateRow(myEntry, myEtag, next);
            }
            else
            {
                ok = await membershipTableProvider.InsertRow(myEntry, next);
            }

            if (ok)
            {
                this.CurrentStatus = newStatus;
                var entries = table.Members.ToDictionary(e => e.Item1.SiloAddress, e => e);
                entries[myEntry.SiloAddress] = Tuple.Create(myEntry, myEtag);
                var updatedTable = new MembershipTableData(entries.Values.ToList(), next);
                await this.ProcessTableUpdate(updatedTable, nameof(TryUpdateMyStatusGlobalOnce));
            }

            return ok;
        }

        private async Task ProcessTableUpdate(MembershipTableData table, string caller, bool logAtInfoLevel = false)
        {
            if (logAtInfoLevel) log.Info(ErrorCode.MembershipReadAll_1, "-ReadAll (called from {0}) Membership table {1}", caller, table.ToString());
            else if (log.IsEnabled(LogLevel.Debug)) log.Debug("-ReadAll (called from {0}) Membership table {1}", caller, table.ToString());

            this.tableCache = table;

            // Even if failed to clean up old entries from the table, still process the new entries. Will retry cleanup next time.
            try
            {
                await CleanupMyTableEntries(table);
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch
            {
                // just eat the exception.
            }

            // Update the current membership snapshot.
            MembershipTableSnapshot previous;
            var updated = MembershipTableSnapshot.Create(this.localSiloDetails, table);
            do
            {
                previous = this.membershipTableSnapshot;
                if (previous.Version >= updated.Version)
                {
                    // This snapshot has been superseded by a later snapshot.
                    // There is no more work to be done by this call.
                    return;
                }
            } while (!ReferenceEquals(Interlocked.CompareExchange(ref this.membershipTableSnapshot, updated, previous), previous));

            do
            {
                var previousUpdate = this.membershipTableUpdates.Current;
                if (previousUpdate.HasValue)
                {
                    if (previousUpdate.Value.Version > updated.Version)
                    {
                        // This update has been superseded by a later update which includes these changes.
                        // There is no more work to be done by this call.
                        return;
                    }
                }
            } while (!this.membershipTableUpdates.TryPublish(updated));

            LogMissedIAmAlives(table);

            this.log.Info(
                ErrorCode.MembershipReadAll_2,
                "-ReadAll (called from {0}, with removed duplicate deads) Membership table: {1}",
                caller,
                table.WithoutDuplicateDeads().ToString());
        }

        private void LogMissedIAmAlives(MembershipTableData table)
        {
            foreach (var pair in table.Members)
            {
                var entry = pair.Item1;
                if (entry.SiloAddress.Equals(MyAddress)) continue;
                if (entry.Status != SiloStatus.Active) continue;

                var now = DateTime.UtcNow;
                var missedSince = entry.HasMissedIAmAlivesSince(this.clusterMembershipOptions, now);
                if (missedSince != null)
                {
                    log.Warn(
                    ErrorCode.MembershipMissedIAmAliveTableUpdate,
                    $"Noticed that silo {entry.SiloAddress} has not updated it's IAmAliveTime table column recently."
                    + $" Last update was at {missedSince}, now is {now}, no update for {now - missedSince}, which is more than {this.clusterMembershipOptions.AllowedIAmAliveMissPeriod}.");
                }
            }
        }

        private async Task<bool> CleanupMyTableEntries(MembershipTableData table)
        {
            var silosToDeclareDead = new List<Tuple<MembershipEntry, string>>();
            foreach (var tuple in table.Members.Where(
                tuple => tuple.Item1.SiloAddress.Endpoint.Equals(MyAddress.Endpoint)))
            {
                var entry = tuple.Item1;
                var siloAddress = entry.SiloAddress;
                
                if (siloAddress.Generation.Equals(MyAddress.Generation))
                {
                    if (entry.Status == SiloStatus.Dead)
                    {
                        var msg = string.Format("I should be Dead according to membership table (in CleanupTableEntries): entry = {0}.", entry.ToFullString());
                        log.Warn(ErrorCode.MembershipFoundMyselfDead2, msg);
                        KillMyselfLocally(msg);
                    }
                    continue;
                }
                
                if (entry.Status == SiloStatus.Dead)
                {
                    if (log.IsEnabled(LogLevel.Trace)) log.Trace("Skipping my previous old Dead entry in membership table: {0}", entry.ToFullString());
                    continue;
                }

                if (log.IsEnabled(LogLevel.Debug)) log.Debug("Temporal anomaly detected in membership table -- Me={0} Other me={1}",
                    MyAddress, siloAddress);

                // Temporal paradox - There is an older clone of this silo in the membership table
                if (siloAddress.Generation < MyAddress.Generation)
                {
                    log.Warn(ErrorCode.MembershipDetectedOlder, "Detected older version of myself - Marking other older clone as Dead -- Current Me={0} Older Me={1}, Old entry= {2}",
                        MyAddress, siloAddress, entry.ToFullString());
                    // Declare older clone of me as Dead.
                    silosToDeclareDead.Add(tuple);   //return DeclareDead(entry, eTag, tableVersion);
                }
                else if (siloAddress.Generation > MyAddress.Generation)
                {
                    // I am the older clone - Newer version of me should survive - I need to kill myself
                    var msg = string.Format("Detected newer version of myself - I am the older clone so I will stop -- Current Me={0} Newer Me={1}, Current entry= {2}",
                        MyAddress, siloAddress, entry.ToFullString());
                    log.Warn(ErrorCode.MembershipDetectedNewer, msg);
                    await this.UpdateMyStatusGlobal(SiloStatus.Dead);
                    KillMyselfLocally(msg);
                    return true; // No point continuing!
                }
            }

            if (silosToDeclareDead.Count == 0) return true;

            if (log.IsEnabled(LogLevel.Debug)) log.Debug("CleanupTableEntries: About to DeclareDead {0} outdated silos in the table: {1}", silosToDeclareDead.Count,
                Utils.EnumerableToString(silosToDeclareDead.Select(tuple => tuple.Item1), entry => entry.ToString()));

            var retValues = new List<bool>();
            var nextVersion = table.Version;

            foreach (var siloData in silosToDeclareDead)
            {
                MembershipEntry entry = siloData.Item1;
                string eTag = siloData.Item2;
                bool ok = await DeclareDead(entry, eTag, nextVersion);
                retValues.Add(ok);
                nextVersion = nextVersion.Next(); // advance the table version (if write succeded, we advanced the version. if failed, someone else did. It is safe anyway).
            }
            return retValues.All(elem => elem);  // if at least one has failed, return false.
        }

        private void KillMyselfLocally(string reason)
        {
            var msg = "I have been told I am dead, so this silo will stop! " + reason;
            log.Error(ErrorCode.MembershipKillMyselfLocally, msg);
            bool alreadyStopping = CurrentStatus.IsTerminating();

            DisposeTimers();
            this.CurrentStatus = SiloStatus.Dead;

            if (!alreadyStopping || !this.clusterMembershipOptions.IsRunningAsUnitTest)
            {
                log.Fail(ErrorCode.MembershipKillMyselfLocally, msg);
            }
            // do not abort in unit tests.
        }

        private Task GossipMyStatus()
        {
            return GossipToOthers(MyAddress, CurrentStatus);
        }

        private Task GossipToOthers(SiloAddress updatedSilo, SiloStatus updatedStatus)
        {
            if (!this.clusterMembershipOptions.UseLivenessGossip) return Task.CompletedTask;
            var tasks = new List<Task>();
            // spread the rumor that some silo has just been marked dead
            foreach (var silo in tableCache.GetSiloStatuses(IsFunctionalForMembership, false, this.MyAddress).Keys)
            {
                if (log.IsEnabled(LogLevel.Trace)) log.Trace("-Sending status update GOSSIP notification about silo {0}, status {1}, to silo {2}", updatedSilo, updatedStatus, silo);
                var remoteOracle = this.grainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipOracleId, silo);
                tasks.Add(remoteOracle
                    .SiloStatusChangeNotification(updatedSilo, updatedStatus)
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Exception exc = task.Exception;
                            log.Error(ErrorCode.MembershipGossipSendFailure, "SiloStatusChangeNotification failed", exc);
                            throw exc;
                        }
                        return true;
                    }));
            }
            return Task.WhenAll(tasks);
        }

        private void OnGetTableUpdateTimer(object data)
        {
            if (log.IsEnabled(LogLevel.Trace)) log.Trace("-{0} fired {1}. CurrentStatus {2}", timerGetTableUpdates.Name, timerGetTableUpdates.GetNumTicks(), CurrentStatus);

            this.timerGetTableUpdates.CheckTimerDelay();

            _ = Task.Run(async () =>
            {
                try
                {
                    var table = await this.membershipTableProvider.ReadAll();
                    await this.ProcessTableUpdate(table, "timer");
                }
                catch (Exception exception)
                {
                    this.log.LogError((int)ErrorCode.MembershipTimerProcessingFailure, "Failed to read membership table: {Exception}", exception);
                }
            });
        }

        public async Task<bool> TryToSuspectOrKill(SiloAddress silo)
        {
            MembershipTableData table = await membershipTableProvider.ReadAll();

            if (log.IsEnabled(LogLevel.Debug)) log.Debug("-TryToSuspectOrKill: Read Membership table {0}", table.ToString());
            if (table.Contains(MyAddress))
            {
                var myEntry = table.Get(MyAddress).Item1;
                if (myEntry.Status == SiloStatus.Dead) // check if the table already knows that I am dead
                {
                    var msg = string.Format("I should be Dead according to membership table (in TryToSuspectOrKill): entry = {0}.", myEntry.ToFullString());
                    log.Warn(ErrorCode.MembershipFoundMyselfDead3, msg);
                    KillMyselfLocally(msg);
                    return true;
                }
            }

            if (!table.Contains(silo))
            {
                // this should not happen ...
                var str = string.Format("-Could not find silo entry for silo {0} in the table.", silo);
                log.Error(ErrorCode.MembershipFailedToReadSilo, str);
                throw new KeyNotFoundException(str);
            }

            var tuple = table.Get(silo);
            var entry = tuple.Item1;
            string eTag = tuple.Item2;
            if (log.IsEnabled(LogLevel.Debug)) log.Debug("-TryToSuspectOrKill {siloAddress}: The current status of {siloAddress} in the table is {status}, its entry is {entry}",
                entry.SiloAddress, // First
                entry.SiloAddress, // Second
                entry.Status, 
                entry.ToFullString());
            // check if the table already knows that this silo is dead
            if (entry.Status == SiloStatus.Dead)
            {
                await this.ProcessTableUpdate(table, "TrySuspectOrKill");
                return true;
            }

            var allVotes = entry.SuspectTimes ?? new List<Tuple<SiloAddress, DateTime>>();

            // get all valid (non-expired) votes
            var freshVotes = entry.GetFreshVotes(DateTime.UtcNow, this.clusterMembershipOptions.DeathVoteExpirationTimeout);

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("-Current number of fresh Voters for {0} is {1}", silo, freshVotes.Count.ToString());

            if (freshVotes.Count >= this.clusterMembershipOptions.NumVotesForDeathDeclaration)
            {
                // this should not happen ...
                var str = string.Format("-Silo {0} is suspected by {1} which is more or equal than {2}, but is not marked as dead. This is a bug!!!",
                    entry.SiloAddress, freshVotes.Count.ToString(), this.clusterMembershipOptions.NumVotesForDeathDeclaration.ToString());
                log.Error(ErrorCode.Runtime_Error_100053, str);
                KillMyselfLocally("Found a bug! Will stop.");
                return false;
            }

            // handle the corner case when the number of active silos is very small (then my only vote is enough)
            int activeSilos = table.GetSiloStatuses(status => status == SiloStatus.Active, true, this.localSiloDetails.SiloAddress).Count;
            // find if I have already voted
            int myVoteIndex = freshVotes.FindIndex(voter => MyAddress.Equals(voter.Item1));

            // Try to kill:
            //  if there is NumVotesForDeathDeclaration votes (including me) to kill - kill.
            //  otherwise, if there is a majority of nodes (including me) voting to kill – kill.
            bool declareDead = false;
            int myAdditionalVote = myVoteIndex == -1 ? 1 : 0;

            if (freshVotes.Count + myAdditionalVote >= this.clusterMembershipOptions.NumVotesForDeathDeclaration)
                declareDead = true;
            
            if (freshVotes.Count + myAdditionalVote >= (activeSilos + 1) / 2)
                declareDead = true;
            
            if (declareDead)
            {
                // kick this silo off
                log.Info(ErrorCode.MembershipMarkingAsDead, 
                    "-Going to mark silo {0} as DEAD in the table #1. I am the last voter: #freshVotes={1}, myVoteIndex = {2}, NumVotesForDeathDeclaration={3} , #activeSilos={4}, suspect list={5}",
                            entry.SiloAddress, 
                            freshVotes.Count, 
                            myVoteIndex,
                            this.clusterMembershipOptions.NumVotesForDeathDeclaration, 
                            activeSilos, 
                            PrintSuspectList(allVotes));
                return await DeclareDead(entry, eTag, table.Version);
            }

            // we still do not have enough votes - need to vote                             
            // find voting place:
            //      update my vote, if I voted previously
            //      OR if the list is not full - just add a new vote
            //      OR overwrite the oldest entry.
            int indexToWrite = allVotes.FindIndex(voter => MyAddress.Equals(voter.Item1));
            if (indexToWrite == -1)
            {
                // My vote is not recorded. Find the most outdated vote if the list is full, and overwrite it
                if (allVotes.Count >= this.clusterMembershipOptions.NumVotesForDeathDeclaration) // if the list is full
                {
                    // The list is full.
                    DateTime minVoteTime = allVotes.Min(voter => voter.Item2); // pick the most outdated vote
                    indexToWrite = allVotes.FindIndex(voter => voter.Item2.Equals(minVoteTime));
                }
            }

            var prevList = allVotes.ToList(); // take a copy
            var now = DateTime.UtcNow;
            if (indexToWrite == -1)
            {
                // if did not find specific place to write (the list is not full), just add a new element to the list
                entry.AddSuspector(MyAddress, now);
            }
            else
            {
                var newEntry = new Tuple<SiloAddress, DateTime>(MyAddress, now);
                entry.SuspectTimes[indexToWrite] = newEntry;
            }
            log.Info(ErrorCode.MembershipVotingForKill,
                "-Putting my vote to mark silo {0} as DEAD #2. Previous suspect list is {1}, trying to update to {2}, eTag={3}, freshVotes is {4}",
                entry.SiloAddress, 
                PrintSuspectList(prevList), 
                PrintSuspectList(entry.SuspectTimes),
                eTag,
                PrintSuspectList(freshVotes));

            // If we fail to update here we will retry later.
            return await membershipTableProvider.UpdateRow(entry, eTag, table.Version.Next());
        }

        private async Task<bool> DeclareDead(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (this.clusterMembershipOptions.LivenessEnabled)
            {
                // add the killer (myself) to the suspect list, for easier diagnosis later on.
                entry.AddSuspector(MyAddress, DateTime.UtcNow);

                if (log.IsEnabled(LogLevel.Debug)) log.Debug("-Going to DeclareDead silo {0} in the table. About to write entry {1}.", entry.SiloAddress, entry.ToFullString());
                entry.Status = SiloStatus.Dead;
                bool ok = await membershipTableProvider.UpdateRow(entry, etag, tableVersion.Next());
                if (ok)
                {
                    if (log.IsEnabled(LogLevel.Debug)) log.Debug("-Successfully updated {0} status to Dead in the Membership table.", entry.SiloAddress);

                    GossipToOthers(entry.SiloAddress, entry.Status).Ignore();
                    var table = await membershipTableProvider.ReadAll();
                    await this.ProcessTableUpdate(table, "DeclareDead");
                    return true;
                }
                
                log.Info(ErrorCode.MembershipMarkDeadWriteFailed, "-Failed to update {0} status to Dead in the Membership table, due to write conflicts. Will retry.", entry.SiloAddress);
                return false;
            }
            
            log.Info(ErrorCode.MembershipCantWriteLivenessDisabled, "-Want to mark silo {0} as DEAD, but will ignore because Liveness is Disabled.", entry.SiloAddress);
            return true;
        }

        private static string PrintSuspectList(IEnumerable<Tuple<SiloAddress, DateTime>> list)
        {
            return Utils.EnumerableToString(list, t => string.Format("<{0}, {1}>", 
                t.Item1, LogFormatter.PrintDate(t.Item2)));
        }

        private void DisposeTimers()
        {
            if (timerGetTableUpdates != null)
            {
                timerGetTableUpdates.Dispose();
                timerGetTableUpdates = null;
            }
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            bool ok = (timerGetTableUpdates != null) && timerGetTableUpdates.CheckTimerFreeze(lastCheckTime);
            return ok;
        }
    }
}
