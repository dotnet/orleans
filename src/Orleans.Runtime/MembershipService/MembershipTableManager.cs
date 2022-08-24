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
using Orleans.Internal;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipTableManager : IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private const int NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS = -1; // unlimited
        private const int NUM_CONDITIONAL_WRITE_ERROR_ATTEMPTS = -1;
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MIN = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan EXP_BACKOFF_CONTENTION_MIN = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MAX = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan EXP_BACKOFF_CONTENTION_MAX = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan GossipTimeout = TimeSpan.FromMilliseconds(3000);

        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly IMembershipGossiper gossiper;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IMembershipTable membershipTableProvider;
        private readonly ILogger log;
        private readonly ISiloLifecycle siloLifecycle;
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly DateTime siloStartTime = DateTime.UtcNow;
        private readonly SiloAddress myAddress;
        private readonly AsyncEnumerable<MembershipTableSnapshot> updates;
        private readonly IAsyncTimer membershipUpdateTimer;

        private MembershipTableSnapshot snapshot;

        public MembershipTableManager(
            ILocalSiloDetails localSiloDetails,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IMembershipTable membershipTable,
            IFatalErrorHandler fatalErrorHandler,
            IMembershipGossiper gossiper,
            ILogger<MembershipTableManager> log,
            IAsyncTimerFactory timerFactory,
            ISiloLifecycle siloLifecycle)
        {
            this.localSiloDetails = localSiloDetails;
            this.membershipTableProvider = membershipTable;
            this.fatalErrorHandler = fatalErrorHandler;
            this.gossiper = gossiper;
            this.clusterMembershipOptions = clusterMembershipOptions.Value;
            this.myAddress = this.localSiloDetails.SiloAddress;
            this.log = log;
            this.siloLifecycle = siloLifecycle;
            var initialEntries = ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.SetItem(this.myAddress, this.CreateLocalSiloEntry(this.CurrentStatus));
            this.snapshot = new MembershipTableSnapshot(
                    MembershipVersion.MinValue,
                    initialEntries);
            this.updates = new AsyncEnumerable<MembershipTableSnapshot>(
                (previous, proposed) => proposed.Version == MembershipVersion.MinValue || proposed.Version > previous.Version,
                this.snapshot)
            {
                OnPublished = update => Interlocked.Exchange(ref this.snapshot, update)
            };

            this.membershipUpdateTimer = timerFactory.Create(
                this.clusterMembershipOptions.TableRefreshTimeout,
                nameof(PeriodicallyRefreshMembershipTable));
        }

        internal Func<DateTime> GetDateTimeUtcNow { get; set; } = () => DateTime.UtcNow;

        public MembershipTableSnapshot MembershipTableSnapshot => this.snapshot;

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipTableUpdates => this.updates;

        public SiloStatus CurrentStatus { get; private set; } = SiloStatus.Created;

        private bool IsStopping => this.siloLifecycle.LowestStoppedStage <= ServiceLifecycleStage.Active;

        private Task pendingRefresh;

        public async Task Refresh()
        {
            var pending = this.pendingRefresh;
            if (pending == null || pending.IsCompleted)
            {
                pending = this.pendingRefresh = this.RefreshInternal(requireCleanup: false);
            }

            await pending;
        }

        public async Task RefreshFromSnapshot(MembershipTableSnapshot snapshot)
        {
            if (snapshot.Version == MembershipVersion.MinValue)
                throw new ArgumentException("Cannot call RefreshFromSnapshot with Version == MembershipVersion.MinValue");

            // Check if a refresh is underway
            var pending = this.pendingRefresh;
            if (pending != null && !pending.IsCompleted)
            {
                await pending;
            }

            this.log.LogInformation("Received cluster membership snapshot via gossip: {Snapshot}", snapshot);

            if (snapshot.Entries.TryGetValue(this.myAddress, out var localSiloEntry))
            {
                if (localSiloEntry.Status == SiloStatus.Dead && this.CurrentStatus != SiloStatus.Dead)
                {
                    this.log.LogWarning(
                        (int)ErrorCode.MembershipFoundMyselfDead1,
                        "I should be Dead according to membership table (in RefreshFromSnapshot). Local entry: {Entry}.",
                        localSiloEntry.ToFullString());
                    this.KillMyselfLocally($"I should be Dead according to membership table (in RefreshFromSnapshot). Local entry: {(localSiloEntry.ToFullString())}.");
                }

                snapshot = MembershipTableSnapshot.Create(localSiloEntry.WithStatus(this.CurrentStatus), snapshot);
            }
            else
            {
                snapshot = MembershipTableSnapshot.Create(this.CreateLocalSiloEntry(this.CurrentStatus), snapshot);
            }

            // If we are behind, let's take directly the snapshot in param
            this.updates.TryPublish(snapshot);
        }

        private async Task<bool> RefreshInternal(bool requireCleanup)
        {
            var table = await this.membershipTableProvider.ReadAll();
            this.ProcessTableUpdate(table, "Refresh");

            bool success;
            try
            {
                success = await this.CleanupMyTableEntries(table);
            }
            catch (Exception exception) when (!requireCleanup)
            {
                success = false;
                this.log.LogWarning(
                    exception,
                    "Exception while trying to clean up my table entries");
            }

            // If cleanup was not required then the cleanup result is ignored.
            return !requireCleanup || success;
        }

        private async Task Start()
        {
            try
            {
                this.log.LogInformation(
                    (int)ErrorCode.MembershipStarting,
                    "MembershipOracle starting on host {HostName} with SiloAddress {SiloAddress} at {StartTime}",
                    this.localSiloDetails.DnsHostName,
                    this.myAddress,
                    LogFormatter.PrintDate(this.siloStartTime));

                // Init the membership table.
                await this.membershipTableProvider.InitializeMembershipTable(true);

                // Perform an initial table read
                var refreshed = await AsyncExecutorWithRetries.ExecuteWithRetries(
                    function: _ => this.RefreshInternal(requireCleanup: true),
                    maxNumSuccessTries: NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS,
                    maxNumErrorTries: NUM_CONDITIONAL_WRITE_ERROR_ATTEMPTS,
                    retryValueFilter: (value, i) => !value,
                    retryExceptionFilter: (exc, i) => true,
                    maxExecutionTime: this.clusterMembershipOptions.MaxJoinAttemptTime,
                    onSuccessBackOff: new ExponentialBackoff(EXP_BACKOFF_CONTENTION_MIN, EXP_BACKOFF_CONTENTION_MAX, EXP_BACKOFF_STEP),
                    onErrorBackOff: new ExponentialBackoff(EXP_BACKOFF_ERROR_MIN, EXP_BACKOFF_ERROR_MAX, EXP_BACKOFF_STEP));

                if (!refreshed)
                {
                    throw new OrleansException("Failed to perform initial membership refresh and cleanup.");
                }

                // read the table and look for my node migration occurrences
                DetectNodeMigration(this.snapshot, this.localSiloDetails.DnsHostName);
            }
            catch (Exception exception)
            {
                this.log.LogError((int)ErrorCode.MembershipFailedToStart, exception, "Membership failed to start");
                throw;
            }
        }

        public async Task UpdateIAmAlive()
        {
            var entry = new MembershipEntry
            {
                SiloAddress = myAddress,
                IAmAliveTime = GetDateTimeUtcNow()
            };

            await this.membershipTableProvider.UpdateIAmAlive(entry);
        }

        private void DetectNodeMigration(MembershipTableSnapshot snapshot, string myHostname)
        {
            string mySiloName = this.localSiloDetails.Name;
            MembershipEntry mostRecentPreviousEntry = null;
            // look for silo instances that are same as me, find most recent with Generation before me.
            foreach (var entry in snapshot.Entries.Select(entry => entry.Value).Where(data => mySiloName.Equals(data.SiloName)))
            {
                bool iAmLater = myAddress.Generation.CompareTo(entry.SiloAddress.Generation) > 0;
                // more recent
                if (iAmLater && (mostRecentPreviousEntry == null || entry.SiloAddress.Generation.CompareTo(mostRecentPreviousEntry.SiloAddress.Generation) > 0))
                    mostRecentPreviousEntry = entry;
            }

            if (mostRecentPreviousEntry != null)
            {
                bool physicalHostChanged = !myHostname.Equals(mostRecentPreviousEntry.HostName) || !myAddress.Endpoint.Equals(mostRecentPreviousEntry.SiloAddress.Endpoint);
                if (physicalHostChanged)
                {
                    log.LogWarning(
                        (int)ErrorCode.MembershipNodeMigrated,
                        "Silo {SiloName} migrated to host {HostName} silo address {SiloAddress} from host {PreviousHostName} silo address {PreviousSiloAddress}.",
                        mySiloName,
                        myHostname,
                        myAddress,
                        mostRecentPreviousEntry.HostName,
                        mostRecentPreviousEntry.SiloAddress);
                }
                else
                {
                    log.LogWarning(
                        (int)ErrorCode.MembershipNodeRestarted,
                        "Silo {SiloName} restarted on same host {HostName} with silo address = {SiloAddress} Previous silo address = {PreviousSiloAddress}",
                        mySiloName,
                        myHostname,
                        myAddress,
                        mostRecentPreviousEntry.SiloAddress);
                }
            }
        }

        private async Task PeriodicallyRefreshMembershipTable()
        {
            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting periodic membership table refreshes");
            try
            {
                var targetMilliseconds = (int)this.clusterMembershipOptions.TableRefreshTimeout.TotalMilliseconds;
                
                TimeSpan? onceOffDelay = RandomTimeSpan.Next(this.clusterMembershipOptions.TableRefreshTimeout);
                while (await this.membershipUpdateTimer.NextTick(onceOffDelay))
                {
                    onceOffDelay = default;

                    try
                    {
                        var stopwatch = ValueStopwatch.StartNew();
                        await this.Refresh();
                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace("Refreshing membership table took {Elapsed}", stopwatch.Elapsed);
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError(
                            (int)ErrorCode.MembershipUpdateIAmAliveFailure,
                            exception,
                            "Failed to refresh membership table, will retry shortly");

                        // Retry quickly
                        onceOffDelay = TimeSpan.FromMilliseconds(200);
                    }
                }
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                this.log.LogError(exception, "Error refreshing membership table");
                this.fatalErrorHandler.OnFatalException(this, nameof(PeriodicallyRefreshMembershipTable), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping periodic membership table refreshes");
            }
        }

        private Task<bool> MembershipExecuteWithRetries(
            Func<int, Task<bool>> taskFunction,
            TimeSpan timeout)
        {
            return MembershipExecuteWithRetries(taskFunction, timeout, (result, i) => result == false);
        }

        private Task<T> MembershipExecuteWithRetries<T>(
            Func<int, Task<T>> taskFunction,
            TimeSpan timeout,
            Func<T, int, bool> retryValueFilter)
        {
            return AsyncExecutorWithRetries.ExecuteWithRetries(
                    taskFunction,
                    NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS,
                    NUM_CONDITIONAL_WRITE_ERROR_ATTEMPTS,
                    retryValueFilter,   // if failed to Update on contention - retry   
                    (exc, i) => true,            // Retry on errors.          
                    timeout,
                    new ExponentialBackoff(EXP_BACKOFF_CONTENTION_MIN, EXP_BACKOFF_CONTENTION_MAX, EXP_BACKOFF_STEP), // how long to wait between successful retries
                    new ExponentialBackoff(EXP_BACKOFF_ERROR_MIN, EXP_BACKOFF_ERROR_MAX, EXP_BACKOFF_STEP)  // how long to wait between error retries
            );
        }

        public async Task UpdateStatus(SiloStatus status)
        {
            bool wasThrownLocally = false;
            int numCalls = 0;
            
            try
            {
                Func<int, Task<bool>> updateMyStatusTask = async counter =>
                {
                    numCalls++;
                    if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Going to try to TryUpdateMyStatusGlobalOnce #{Attempt}", counter);
                    return await TryUpdateMyStatusGlobalOnce(status);  // function to retry
                };
                
                if (status == SiloStatus.Dead && this.membershipTableProvider is SystemTargetBasedMembershipTable)
                {
                    // SystemTarget-based membership may not be accessible at this stage, so allow for one quick attempt to update
                    // the status before continuing regardless of the outcome.
                    var updateTask = updateMyStatusTask(0);
                    updateTask.Ignore();
                    await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(500)), updateTask);

                    var gossipTask = this.GossipToOthers(this.myAddress, status);
                    gossipTask.Ignore();
                    await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(500)), gossipTask);

                    this.CurrentStatus = status;
                    return;
                }

                bool ok = await MembershipExecuteWithRetries(updateMyStatusTask, this.clusterMembershipOptions.MaxJoinAttemptTime);

                if (ok)
                {
                    if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Silo {SiloAddress} Successfully updated my Status in the membership table to {Status}", myAddress, status);

                    var gossipTask = this.GossipToOthers(this.myAddress, status);
                    gossipTask.Ignore();
                    var cancellation = new CancellationTokenSource();
                    var timeoutTask = Task.Delay(GossipTimeout, cancellation.Token);
                    var task = await Task.WhenAny(gossipTask, timeoutTask);
                    if (ReferenceEquals(task, timeoutTask))
                    {
                        if (status.IsTerminating())
                        {
                            this.log.LogWarning("Timed out while gossiping status to other silos after {Timeout}", GossipTimeout);
                        }
                        else if (this.log.IsEnabled(LogLevel.Debug))
                        {
                            this.log.LogDebug("Timed out while gossiping status to other silos after {Timeout}", GossipTimeout);
                        }
                    }
                    else
                    {
                        cancellation.Cancel();
                    }
                }
                else
                {
                    wasThrownLocally = true;
                    log.LogError(
                        (int)ErrorCode.MembershipFailedToWriteConditional,
                        "Silo {MyAddress} failed to update its status to {Status} in the membership table due to write contention on the table after {NumCalls} attempts.",
                        myAddress,
                        status,
                        numCalls);
                    throw new OrleansException($"Silo {myAddress} failed to update its status to {status} in the membership table due to write contention on the table after {numCalls} attempts.");
                }
            }
            catch (Exception exc)  when (!wasThrownLocally)
            {
                log.LogError(
                    (int)ErrorCode.MembershipFailedToWrite,
                    exc,
                    "Silo {MyAddress} failed to update its status to {Status} in the table due to failures (socket failures or table read/write failures) after {NumCalls} attempts",
                    myAddress,
                    status,
                    numCalls);
                throw new OrleansException($"Silo {myAddress} failed to update its status to {status} in the table due to failures (socket failures or table read/write failures) after {numCalls} attempts", exc);
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

            if (log.IsEnabled(LogLevel.Debug))
                log.LogDebug(
                    "TryUpdateMyStatusGlobalOnce: Read{Selection} Membership table {Table}",
                    (newStatus.Equals(SiloStatus.Active) ? "All" : " my entry from"),
                    table.ToString());
            LogMissedIAmAlives(table);
            var (myEntry, myEtag) = this.GetOrCreateLocalSiloEntry(table, newStatus);

            if (myEntry.Status == SiloStatus.Dead && myEntry.Status != newStatus)
            {
                this.log.LogWarning(
                    (int)ErrorCode.MembershipFoundMyselfDead1,
                    "I should be Dead according to membership table (in TryUpdateMyStatusGlobalOnce): Entry = {Entry}.",
                    myEntry.ToFullString());
                this.KillMyselfLocally($"I should be Dead according to membership table (in TryUpdateMyStatusGlobalOnce): Entry = {(myEntry.ToFullString())}.");
                return true;
            }

            var now = GetDateTimeUtcNow();
            if (newStatus == SiloStatus.Dead)
                myEntry.AddSuspector(myAddress, now); // add the killer (myself) to the suspect list, for easier diagnostics later on.

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
                this.ProcessTableUpdate(updatedTable, nameof(TryUpdateMyStatusGlobalOnce));
            }

            return ok;
        }

        private (MembershipEntry Entry, string ETag) GetOrCreateLocalSiloEntry(MembershipTableData table, SiloStatus currentStatus)
        {
            if (table.TryGet(myAddress) is { } myTuple)
            {
                return (myTuple.Item1.Copy(), myTuple.Item2);
            }

            var result = CreateLocalSiloEntry(currentStatus);
            return (result, null);
        }

        private MembershipEntry CreateLocalSiloEntry(SiloStatus currentStatus)
        {
            return new MembershipEntry
            {
                SiloAddress = this.localSiloDetails.SiloAddress,

                HostName = this.localSiloDetails.DnsHostName,
                SiloName = this.localSiloDetails.Name,

                Status = currentStatus,
                ProxyPort = this.localSiloDetails.GatewayAddress?.Endpoint?.Port ?? 0,

                RoleName = (Assembly.GetEntryAssembly() ?? typeof(MembershipTableManager).Assembly).GetName().Name,

                SuspectTimes = new List<Tuple<SiloAddress, DateTime>>(),
                StartTime = this.siloStartTime,
                IAmAliveTime = GetDateTimeUtcNow()
            };
        }

        private void ProcessTableUpdate(MembershipTableData table, string caller)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));
            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug($"{nameof(ProcessTableUpdate)} (called from {{Caller}}) membership table {{Table}}", caller, table.ToString());

            // Update the current membership snapshot.
            var (localSiloEntry, _) = this.GetOrCreateLocalSiloEntry(table, this.CurrentStatus);
            var updated = MembershipTableSnapshot.Create(localSiloEntry, table);

            if (this.updates.TryPublish(updated))
            {
                this.LogMissedIAmAlives(table);

                if (this.log.IsEnabled(LogLevel.Debug))
                {
                    this.log.LogDebug(
                        (int)ErrorCode.MembershipReadAll_2,
                        $"{nameof(ProcessTableUpdate)} (called from {{Caller}}) membership table: {{Table}}",
                        caller,
                        table.WithoutDuplicateDeads().ToString());
                }
            }
        }

        private void LogMissedIAmAlives(MembershipTableData table)
        {
            foreach (var pair in table.Members)
            {
                var entry = pair.Item1;
                if (entry.SiloAddress.Equals(myAddress)) continue;
                if (entry.Status != SiloStatus.Active) continue;

                var now = GetDateTimeUtcNow();
                var missedSince = entry.HasMissedIAmAlivesSince(this.clusterMembershipOptions, now);
                if (missedSince != null)
                {
                    log.LogWarning(
                        (int)ErrorCode.MembershipMissedIAmAliveTableUpdate,
                        "Noticed that silo {SiloAddress} has not updated it's IAmAliveTime table column recently."
                        + " Last update was at {LastUpdateTime}, now is {CurrentTime}, no update for {SinceUpdate}, which is more than {AllowedIAmAliveMissPeriod}.",
                        entry.SiloAddress,
                        missedSince,
                        now,
                        now - missedSince,
                        clusterMembershipOptions.AllowedIAmAliveMissPeriod);
                }
            }
        }

        private async Task<bool> CleanupMyTableEntries(MembershipTableData table)
        {
            if (this.IsStopping) return true;

            var silosToDeclareDead = new List<Tuple<MembershipEntry, string>>();
            foreach (var tuple in table.Members.Where(
                tuple => tuple.Item1.SiloAddress.Endpoint.Equals(myAddress.Endpoint)))
            {
                var entry = tuple.Item1;
                var siloAddress = entry.SiloAddress;
                
                if (siloAddress.Generation.Equals(myAddress.Generation))
                {
                    if (entry.Status == SiloStatus.Dead)
                    {
                        log.LogWarning((int)ErrorCode.MembershipFoundMyselfDead2, "I should be Dead according to membership table (in CleanupTableEntries): entry = {Entry}.", entry.ToFullString());
                        KillMyselfLocally($"I should be Dead according to membership table (in CleanupTableEntries): entry = {(entry.ToFullString())}.");
                    }
                    continue;
                }
                
                if (entry.Status == SiloStatus.Dead)
                {
                    if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("Skipping my previous old Dead entry in membership table: {Entry}", entry.ToFullString());
                    continue;
                }

                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug(
                        "Temporal anomaly detected in membership table -- Me={SiloAddress} Other me={OtherSiloAddress}",
                        myAddress,
                        siloAddress);

                // Temporal paradox - There is an older clone of this silo in the membership table
                if (siloAddress.Generation < myAddress.Generation)
                {
                    log.LogWarning(
                        (int)ErrorCode.MembershipDetectedOlder,
                        "Detected older version of myself - Marking other older clone as Dead -- Current Me={LocalSiloAddress} Older Me={OlderSiloAddress}, Old entry={Entry}",
                        myAddress,
                        siloAddress,
                        entry.ToString());
                    // Declare older clone of me as Dead.
                    silosToDeclareDead.Add(tuple);   //return DeclareDead(entry, eTag, tableVersion);
                }
                else if (siloAddress.Generation > myAddress.Generation)
                {
                    // I am the older clone - Newer version of me should survive - I need to kill myself
                    log.LogWarning(
                        (int)ErrorCode.MembershipDetectedNewer,
                        "Detected newer version of myself - I am the older clone so I will stop -- Current Me={LocalSiloAddress} Newer Me={NewerSiloAddress}, Current entry={Entry}",
                        myAddress,
                        siloAddress,
                        entry.ToString());
                    await this.UpdateStatus(SiloStatus.Dead);
                    KillMyselfLocally($"Detected newer version of myself - I am the older clone so I will stop -- Current Me={myAddress} Newer Me={siloAddress}, Current entry={entry}");
                    return true; // No point continuing!
                }
            }

            if (silosToDeclareDead.Count == 0) return true;

            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("CleanupTableEntries: About to DeclareDead {Count} outdated silos in the table: {Silos}", silosToDeclareDead.Count,
                Utils.EnumerableToString(silosToDeclareDead.Select(tuple => tuple.Item1)));

            var result = true;
            var nextVersion = table.Version;

            foreach (var siloData in silosToDeclareDead)
            {
                MembershipEntry entry = siloData.Item1;
                string eTag = siloData.Item2;
                bool ok = await DeclareDead(entry, eTag, nextVersion, GetDateTimeUtcNow());
                if (!ok) result = false;
                nextVersion = nextVersion.Next(); // advance the table version (if write succeded, we advanced the version. if failed, someone else did. It is safe anyway).
            }

            return result;
        }

        private void KillMyselfLocally(string reason)
        {
            log.LogError((int)ErrorCode.MembershipKillMyselfLocally, "I have been told I am dead, so this silo will stop! Reason: {Reason}", reason);
            this.CurrentStatus = SiloStatus.Dead;
            this.fatalErrorHandler.OnFatalException(this, $"I have been told I am dead, so this silo will stop! Reason: {reason}", null);
        }

        private async Task GossipToOthers(SiloAddress updatedSilo, SiloStatus updatedStatus)
        {
            if (!this.clusterMembershipOptions.UseLivenessGossip) return;

            var now = GetDateTimeUtcNow();
            var gossipPartners = new List<SiloAddress>();
            foreach (var item in this.MembershipTableSnapshot.Entries)
            {
                var entry = item.Value;
                if (entry.SiloAddress.IsSameLogicalSilo(this.myAddress)) continue;
                if (!IsFunctionalForMembership(entry.Status)) continue;
                if (entry.HasMissedIAmAlivesSince(this.clusterMembershipOptions, now) != default) continue;

                gossipPartners.Add(entry.SiloAddress);

                bool IsFunctionalForMembership(SiloStatus status)
                {
                    return status == SiloStatus.Active || status == SiloStatus.ShuttingDown || status == SiloStatus.Stopping;
                }
            }

            try
            {
                await this.gossiper.GossipToRemoteSilos(gossipPartners, MembershipTableSnapshot, updatedSilo, updatedStatus);
            }
            catch (Exception exception)
            {
                this.log.LogWarning(exception, "Error while gossiping status to other silos");
            }
        }

        public async Task<bool> TryKill(SiloAddress silo)
        {
            var table = await membershipTableProvider.ReadAll();

            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("TryKill: Read Membership table {0}", table.ToString());
            }

            if (this.IsStopping)
            {
                this.log.LogInformation(
                    (int)ErrorCode.MembershipFoundMyselfDead3,
                    "Ignoring call to TryKill for silo {Silo} since the local silo is stopping",
                    silo);
                return true;
            }

            var (localSiloEntry, _) = this.GetOrCreateLocalSiloEntry(table, this.CurrentStatus);
            if (localSiloEntry.Status == SiloStatus.Dead)
            {
                var msg = string.Format("I should be Dead according to membership table (in TryKill): entry = {0}.", localSiloEntry.ToFullString());
                log.LogWarning((int)ErrorCode.MembershipFoundMyselfDead3, msg);
                KillMyselfLocally(msg);
                return true;
            }

            if (table.TryGet(silo) is not { } tuple)
            {
                var str = $"Could not find silo entry for silo {silo} in the table.";
                log.LogError((int)ErrorCode.MembershipFailedToReadSilo, str);
                throw new KeyNotFoundException(str);
            }

            var entry = tuple.Item1.Copy();
            string eTag = tuple.Item2;

            // Check if the table already knows that this silo is dead
            if (entry.Status == SiloStatus.Dead)
            {
                this.ProcessTableUpdate(table, "TryKill");
                return true;
            }

            log.LogInformation(
                (int)ErrorCode.MembershipMarkingAsDead,
                "Going to mark silo {SiloAddress} dead as a result of a call to TryKill",
                entry.SiloAddress);
            return await DeclareDead(entry, eTag, table.Version, GetDateTimeUtcNow());
        }

        public async Task<bool> TryToSuspectOrKill(SiloAddress silo)
        {
            var table = await membershipTableProvider.ReadAll();
            var now = GetDateTimeUtcNow();

            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("TryToSuspectOrKill: Read Membership table {Table}", table.ToString());

            if (this.IsStopping)
            {
                this.log.LogInformation(
                    (int)ErrorCode.MembershipFoundMyselfDead3,
                    "Ignoring call to TrySuspectOrKill for silo {Silo} since the local silo is dead",
                    silo);
                return true;
            }

            var (localSiloEntry, _) = this.GetOrCreateLocalSiloEntry(table, this.CurrentStatus);
            if (localSiloEntry.Status == SiloStatus.Dead)
            {
                var localSiloEntryDetails = localSiloEntry.ToFullString();
                log.LogWarning(
                    (int)ErrorCode.MembershipFoundMyselfDead3,
                    "I should be Dead according to membership table (in TryToSuspectOrKill): entry = {Entry}.",
                    localSiloEntryDetails);
                KillMyselfLocally($"I should be Dead according to membership table (in TryToSuspectOrKill): entry = {localSiloEntryDetails}.");
                return true;
            }

            if (table.TryGet(silo) is not { } tuple)
            {
                // this should not happen ...
                log.LogError((int)ErrorCode.MembershipFailedToReadSilo, "Could not find silo entry for silo {Silo} in the table.", silo);
                throw new KeyNotFoundException($"Could not find silo entry for silo {silo} in the table.");
            }

            var entry = tuple.Item1.Copy();
            string eTag = tuple.Item2;
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug(
                    "TryToSuspectOrKill {SiloAddress}: The current status of {SiloAddress} in the table is {Status}, its entry is {Entry}",
                    entry.SiloAddress, // First
                    entry.SiloAddress, // Second
                    entry.Status,
                    entry.ToString());
            }

            // Check if the table already knows that this silo is dead
            if (entry.Status == SiloStatus.Dead)
            {
                this.ProcessTableUpdate(table, "TrySuspectOrKill");
                return true;
            }

            // Get all valid (non-expired) votes
            var freshVotes = entry.GetFreshVotes(now, this.clusterMembershipOptions.DeathVoteExpirationTimeout);

            if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("Current number of fresh voters for {SiloAddress} is {FreshVotes}", silo, freshVotes.Count.ToString());

            if (freshVotes.Count >= this.clusterMembershipOptions.NumVotesForDeathDeclaration)
            {
                // this should not happen ...
                log.LogError(
                    (int)ErrorCode.Runtime_Error_100053,
                    "Silo {SiloAddress} is suspected by {SuspectorCount} which is more or equal than {NumVotesForDeathDeclaration}, but is not marked as dead. This is a bug!!!",
                    entry.SiloAddress,
                    freshVotes.Count.ToString(),
                    this.clusterMembershipOptions.NumVotesForDeathDeclaration.ToString());

                KillMyselfLocally("Found a bug! Will stop.");
                return false;
            }

            // Try to add our vote to the list and tally the fresh votes again.
            var prevList = entry.SuspectTimes?.ToList() ?? new List<Tuple<SiloAddress, DateTime>>();
            entry.AddOrUpdateSuspector(myAddress, now, clusterMembershipOptions.NumVotesForDeathDeclaration);
            freshVotes = entry.GetFreshVotes(now, this.clusterMembershipOptions.DeathVoteExpirationTimeout);

            // Determine if there are enough votes to evict the silo.
            // Handle the corner case when the number of active silos is very small (then my only vote is enough)
            int activeSilos = table.GetSiloStatuses(status => status == SiloStatus.Active, true, myAddress).Count;
            if (freshVotes.Count >= clusterMembershipOptions.NumVotesForDeathDeclaration || freshVotes.Count >= (activeSilos + 1) / 2)
            {
                // Find the local silo's vote index
                int myVoteIndex = freshVotes.FindIndex(voter => myAddress.Equals(voter.Item1));

                // Kick this silo off
                log.LogInformation(
                    (int)ErrorCode.MembershipMarkingAsDead,
                    "Going to mark silo {SiloAddress} as DEAD in the table #1. This silo is the last voter: #FreshVotes={FreshVotes}, MyVoteIndex = {MyVoteIndex}, NumVotesForDeathDeclaration={NumVotesForDeathDeclaration} , #ActiveSilos={ActiveSiloCount}, suspect list={SuspectingSilos}",
                    entry.SiloAddress,
                    freshVotes.Count,
                    myVoteIndex,
                    this.clusterMembershipOptions.NumVotesForDeathDeclaration,
                    activeSilos,
                    PrintSuspectList(entry.SuspectTimes));

                return await DeclareDead(entry, eTag, table.Version, now);
            }

            log.LogInformation(
                (int)ErrorCode.MembershipVotingForKill,
                "Putting my vote to mark silo {SiloAddress} as DEAD #2. Previous suspect list is {PreviousSuspectors}, trying to update to {Suspectors}, ETag={ETag}, FreshVotes is {FreshVotes}",
                entry.SiloAddress, 
                PrintSuspectList(prevList), 
                PrintSuspectList(entry.SuspectTimes),
                eTag,
                PrintSuspectList(freshVotes));

            // If we fail to update here we will retry later.
            var ok = await membershipTableProvider.UpdateRow(entry, eTag, table.Version.Next());
            if (ok)
            {
                table = await membershipTableProvider.ReadAll();
                this.ProcessTableUpdate(table, "TrySuspectOrKill");

                // Gossip using the local silo status, since this is just informational to propagate the suspicion vote.
                GossipToOthers(localSiloEntry.SiloAddress, localSiloEntry.Status).Ignore();
            }

            return ok;

            string PrintSuspectList(IEnumerable<Tuple<SiloAddress, DateTime>> list)
            {
                return Utils.EnumerableToString(list, t => $"<{t.Item1}, {LogFormatter.PrintDate(t.Item2)}>");
            }
        }

        private async Task<bool> DeclareDead(MembershipEntry entry, string etag, TableVersion tableVersion, DateTime time)
        {
            if (this.clusterMembershipOptions.LivenessEnabled)
            {
                entry = entry.Copy();

                // Add the killer (myself) to the suspect list, for easier diagnosis later on.
                entry.AddSuspector(myAddress, time);

                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Going to DeclareDead silo {SiloAddress} in the table. About to write entry {Entry}.", entry.SiloAddress, entry.ToString());
                entry.Status = SiloStatus.Dead;
                bool ok = await membershipTableProvider.UpdateRow(entry, etag, tableVersion.Next());
                if (ok)
                {
                    if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Successfully updated {SiloAddress} status to Dead in the membership table.", entry.SiloAddress);

                    var table = await membershipTableProvider.ReadAll();
                    this.ProcessTableUpdate(table, "DeclareDead");
                    GossipToOthers(entry.SiloAddress, entry.Status).Ignore();
                    return true;
                }
                
                log.LogInformation(
                    (int)ErrorCode.MembershipMarkDeadWriteFailed,
                    "Failed to update {SiloAddress} status to Dead in the membership table, due to write conflicts. Will retry.",
                    entry.SiloAddress);
                return false;
            }
            
            log.LogInformation((int)ErrorCode.MembershipCantWriteLivenessDisabled, "Want to mark silo {SiloAddress} as DEAD, but will ignore because Liveness is Disabled.", entry.SiloAddress);
            return true;
        }

        bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, out string reason) => this.membershipUpdateTimer.CheckHealth(lastCheckTime, out reason);

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>(1);
            lifecycle.Subscribe(
                nameof(MembershipTableManager),
                ServiceLifecycleStage.RuntimeGrainServices,
                OnRuntimeGrainServicesStart,
                OnRuntimeGrainServicesStop);

            async Task OnRuntimeGrainServicesStart(CancellationToken ct)
            {
                await Task.Run(() => this.Start());
                tasks.Add(Task.Run(() => this.PeriodicallyRefreshMembershipTable()));
            }

            async Task OnRuntimeGrainServicesStop(CancellationToken ct)
            {
                this.membershipUpdateTimer.Dispose();

                // Allow some minimum time for graceful shutdown.
                var gracePeriod = Task.WhenAll(Task.Delay(ClusterMembershipOptions.ClusteringShutdownGracePeriod), ct.WhenCancelled());
                await Task.WhenAny(gracePeriod, Task.WhenAll(tasks));
            }
        }

        public void Dispose()
        {
            this.updates.Dispose();
            this.membershipUpdateTimer.Dispose();
        }
    }
}
