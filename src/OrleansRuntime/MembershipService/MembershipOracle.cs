using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipOracle : SystemTarget, IMembershipOracle, IMembershipService
    {
        private readonly MembershipTableFactory membershipTableFactory;
        private IMembershipTable membershipTableProvider;
        private readonly MembershipOracleData membershipOracleData;
        private Dictionary<SiloAddress, int> probedSilos;  // map from currently probed silos to the number of failed probes
        private readonly LoggerImpl logger;
        private readonly ClusterConfiguration orleansConfig;
        private readonly NodeConfiguration nodeConfig;
        private SiloAddress MyAddress { get { return membershipOracleData.MyAddress; } }
        private GrainTimer timerGetTableUpdates;
        private GrainTimer timerProbeOtherSilos;
        private GrainTimer timerIAmAliveUpdateInTable;
        private int pingCounter; // for logging and diagnostics only

        private const int NUM_CONDITIONAL_WRITE_CONTENTION_ATTEMPTS = -1; // unlimited
        private const int NUM_CONDITIONAL_WRITE_ERROR_ATTEMPTS = -1;
        private static readonly TimeSpan EXP_BACKOFF_ERROR_MIN = SiloMessageSender.CONNECTION_RETRY_DELAY;
        private static readonly TimeSpan EXP_BACKOFF_CONTENTION_MIN = TimeSpan.FromMilliseconds(100);
        private static TimeSpan EXP_BACKOFF_ERROR_MAX;
        private static TimeSpan EXP_BACKOFF_CONTENTION_MAX; // set based on config
        private static readonly TimeSpan EXP_BACKOFF_STEP = TimeSpan.FromMilliseconds(1000);

        public SiloStatus CurrentStatus { get { return membershipOracleData.CurrentStatus; } } // current status of this silo.

        public string SiloName { get { return membershipOracleData.SiloName; } }
        public SiloAddress SiloAddress { get { return membershipOracleData.MyAddress; } }
        private TimeSpan AllowedIAmAliveMissPeriod { get { return orleansConfig.Globals.IAmAliveTablePublishTimeout.Multiply(orleansConfig.Globals.NumMissedTableIAmAliveLimit); } }

        public MembershipOracle(Silo silo, MembershipTableFactory membershipTableFactory)
            : base(Constants.MembershipOracleId, silo.SiloAddress)
        {
            this.membershipTableFactory = membershipTableFactory;
            logger = LogManager.GetLogger("MembershipOracle");
            membershipOracleData = new MembershipOracleData(silo, logger);
            probedSilos = new Dictionary<SiloAddress, int>();
            orleansConfig = silo.OrleansConfig;
            nodeConfig = silo.LocalConfig;
            pingCounter = 0;
            TimeSpan backOffMax = StandardExtensions.Max(EXP_BACKOFF_STEP.Multiply(orleansConfig.Globals.ExpectedClusterSize), SiloMessageSender.CONNECTION_RETRY_DELAY.Multiply(2));
            EXP_BACKOFF_CONTENTION_MAX = backOffMax;
            EXP_BACKOFF_ERROR_MAX = backOffMax;
        }

        #region ISiloStatusOracle Members

        public async Task Start()
        {
            try
            {
                logger.Info(ErrorCode.MembershipStarting, "MembershipOracle starting on host = " + membershipOracleData.MyHostname + " address = " + MyAddress.ToLongString() + " at " + LogFormatter.PrintDate(membershipOracleData.SiloStartTime) + ", backOffMax = " + EXP_BACKOFF_CONTENTION_MAX);

                // Create the membership table.
                this.membershipTableProvider = await this.membershipTableFactory.GetMembershipTable();

                // randomly delay the startup, so not all silos write to the table at once.
                // Use random time not larger than MaxJoinAttemptTime, one minute and 0.5sec*ExpectedClusterSize;
                var random = new SafeRandom();
                var maxDelay = TimeSpan.FromMilliseconds(500).Multiply(orleansConfig.Globals.ExpectedClusterSize);
                maxDelay = StandardExtensions.Min(maxDelay, StandardExtensions.Min(orleansConfig.Globals.MaxJoinAttemptTime, TimeSpan.FromMinutes(1)));
                var randomDelay = random.NextTimeSpan(maxDelay);
                await Task.Delay(randomDelay);

                // first, cleanup all outdated entries of myself from the table
                await CleanupTable();

                // write myself to the table
                await UpdateMyStatusGlobal(SiloStatus.Joining);

                StartIAmAliveUpdateTimer();

                // read the table and look for my node migration occurrences
                await DetectNodeMigration(membershipOracleData.MyHostname);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.MembershipFailedToStart, "MembershipFailedToStart", exc);
                throw;
            }
        }

        private async Task DetectNodeMigration(string myHostname)
        {
            MembershipTableData table = await membershipTableProvider.ReadAll();
            if (logger.IsVerbose) logger.Verbose("-ReadAll Membership table {0}", table.ToString());
            CheckMissedIAmAlives(table);

            string mySiloName = nodeConfig.SiloName;
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
                    string error = String.Format("Silo {0} migrated from host {1} silo address {2} to host {3} silo address {4}.",
                        mySiloName, myHostname, MyAddress.ToLongString(), mostRecentPreviousEntry.HostName, mostRecentPreviousEntry.SiloAddress.ToLongString());
                    logger.Warn(ErrorCode.MembershipNodeMigrated, error);
                }
                else
                {
                    string error = String.Format("Silo {0} restarted on same host {1} New silo address = {2} Previous silo address = {3}",
                        mySiloName, myHostname, MyAddress.ToLongString(), mostRecentPreviousEntry.SiloAddress.ToLongString());
                    logger.Warn(ErrorCode.MembershipNodeRestarted, error);
                }
            }
        }

        public async Task BecomeActive()
        {
            logger.Info(ErrorCode.MembershipBecomeActive, "-BecomeActive");

            // write myself to the table
            // read the table and store locally the list of live silos
            try
            {
                await UpdateMyStatusGlobal(SiloStatus.Active);

                MembershipTableData table = await membershipTableProvider.ReadAll();
                await ProcessTableUpdate(table, "BecomeActive", true);
                    
                GossipMyStatus(); // only now read and stored the table locally.

                Action configure = () =>
                {
                    var random = new SafeRandom();
                    var randomTableOffset = random.NextTimeSpan(orleansConfig.Globals.TableRefreshTimeout);
                    var randomProbeOffset = random.NextTimeSpan(orleansConfig.Globals.ProbeTimeout);
                    if (timerGetTableUpdates != null)
                        timerGetTableUpdates.Dispose();

                    timerGetTableUpdates = GrainTimer.FromTimerCallback(
                        OnGetTableUpdateTimer, null, randomTableOffset, orleansConfig.Globals.TableRefreshTimeout, "Membership.ReadTableTimer");
                    
                    timerGetTableUpdates.Start();

                    if (timerProbeOtherSilos != null)
                        timerProbeOtherSilos.Dispose();

                    timerProbeOtherSilos = GrainTimer.FromTimerCallback(
                        OnProbeOtherSilosTimer, null, randomProbeOffset, orleansConfig.Globals.ProbeTimeout, "Membership.ProbeTimer");
                    
                    timerProbeOtherSilos.Start();
                };
                orleansConfig.OnConfigChange(
                    "Globals/Liveness", () => InsideRuntimeClient.Current.Scheduler.RunOrQueueAction(configure, SchedulingContext), false);

                configure();
                logger.Info(ErrorCode.MembershipFinishBecomeActive, "-Finished BecomeActive.");
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.MembershipFailedToBecomeActive, "BecomeActive failed", exc);
                throw;
            }
        }

        private void StartIAmAliveUpdateTimer()
        {
            logger.Info(ErrorCode.MembershipStartingIAmAliveTimer, "Starting IAmAliveUpdateTimer.");
        
            if (timerIAmAliveUpdateInTable != null)
                timerIAmAliveUpdateInTable.Dispose();

            timerIAmAliveUpdateInTable = GrainTimer.FromTimerCallback(
                OnIAmAliveUpdateInTableTimer, null, TimeSpan.Zero, orleansConfig.Globals.IAmAliveTablePublishTimeout, "Membership.IAmAliveTimer");

            timerIAmAliveUpdateInTable.Start();
        }

        public async Task ShutDown()
        {
            const string operation = "ShutDown";
            logger.Info(ErrorCode.MembershipShutDown, "-" + operation);
            try
            {
                await UpdateMyStatusGlobal(SiloStatus.ShuttingDown);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.MembershipFailedToShutdown, "Error doing " + operation, exc);
                throw;
            }
        }

        public async Task Stop()
        {
            const string operation = "Stop";
            logger.Info(ErrorCode.MembershipStop, "-" + operation);
            try
            {
                await UpdateMyStatusGlobal(SiloStatus.Stopping);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.MembershipFailedToStop, "Error doing " + operation, exc);
                throw;
            }
        }

        public async Task KillMyself()
        {
            const string operation = "KillMyself";
            logger.Info(ErrorCode.MembershipKillMyself, "-" + operation);
            try
            {
                DisposeTimers();
                await UpdateMyStatusGlobal(SiloStatus.Dead);
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.MembershipFailedToKillMyself, "Error doing " + operation, exc);
                throw;
            }
        }

        // ONLY access localTableCopy and not the localTable, to prevent races, as this method may be called outside the turn.
        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
        {
            return membershipOracleData.GetApproximateSiloStatus(siloAddress);
        }

        // ONLY access localTableCopy or localTableCopyOnlyActive and not the localTable, to prevent races, as this method may be called outside the turn.
        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            return membershipOracleData.GetApproximateSiloStatuses(onlyActive);
        }

        // ONLY access gatewaysLocalCopy to prevent races
        public IReadOnlyList<SiloAddress> GetApproximateMultiClusterGateways()
        {
            return membershipOracleData.GetApproximateMultiClusterGateways();
        }

        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            return membershipOracleData.TryGetSiloName(siloAddress, out siloName);
        }

        private bool IsFunctionalMBR(SiloStatus status)
        {
            return status == SiloStatus.Active || status == SiloStatus.ShuttingDown || status == SiloStatus.Stopping;
        }

        public bool IsFunctionalDirectory(SiloAddress silo)
        {
            if (silo.Equals(MyAddress)) return true;

            var status = membershipOracleData.GetApproximateSiloStatus(silo);
            return !status.IsTerminating();
        }

        public bool IsDeadSilo(SiloAddress silo)
        {
            if (silo.Equals(MyAddress)) return false;

            var status = membershipOracleData.GetApproximateSiloStatus(silo);
            return status == SiloStatus.Dead;
        }

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer)
        {
            return membershipOracleData.SubscribeToSiloStatusEvents(observer);
        }

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer)
        {
            return membershipOracleData.UnSubscribeFromSiloStatusEvents(observer);
        }

        #endregion


        #region IMembershipService Members

        // Treat this gossip msg as a trigger to read the table (and just ignore the input parameters).
        // This simplified a lot of the races when we get gossip info which is outdated with the table truth.
        public async Task SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (logger.IsVerbose2) logger.Verbose2("-Received GOSSIP SiloStatusChangeNotification about {0} status {1}. Going to read the table.", updatedSilo, status);
            if (IsFunctionalMBR(CurrentStatus))
            {
                try
                {
                    MembershipTableData table = await membershipTableProvider.ReadAll();
                    await ProcessTableUpdate(table, "gossip");
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.MembershipGossipProcessingFailure, 
                        "Error doing SiloStatusChangeNotification", exc);
                    throw;
                }
            }
        }

        public Task Ping(int pingNumber)
        {
            // do not do anything here -- simply returning back will indirectly notify the prober that this silo is alive
            return TaskDone.Done;
        }

        #endregion

        #region Table update/insert processing

        private static Task<bool> MembershipExecuteWithRetries(
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
                    new ExponentialBackoff(EXP_BACKOFF_CONTENTION_MIN, EXP_BACKOFF_CONTENTION_MAX, EXP_BACKOFF_STEP), // how long to wait between successful retries
                    new ExponentialBackoff(EXP_BACKOFF_ERROR_MIN, EXP_BACKOFF_ERROR_MAX, EXP_BACKOFF_STEP)  // how long to wait between error retries
            );
        }

        private Task CleanupTable()
        {
            Func<int, Task<bool>> cleanupTableEntriesTask = async counter => 
            {
                if (logger.IsVerbose) logger.Verbose("-Attempting CleanupTableEntries #{0}", counter);
                MembershipTableData table = await membershipTableProvider.ReadAll();
                logger.LogWithoutBulkingAndTruncating(Severity.Info, ErrorCode.MembershipReadAll_Cleanup, "-CleanupTable called on silo startup. Membership table {0}",
                    table.ToString());

                return await CleanupTableEntries(table);
            };

            return MembershipExecuteWithRetries(cleanupTableEntriesTask, orleansConfig.Globals.MaxJoinAttemptTime);
        }

        private async Task UpdateMyStatusGlobal(SiloStatus status)
        {
            string errorString = null;
            int numCalls = 0;

            try
            {
                Func<int, Task<bool>> updateMyStatusTask = async counter =>
                {
                    numCalls++;
                    if (logger.IsVerbose) logger.Verbose("-Going to try to TryUpdateMyStatusGlobalOnce #{0}", counter);
                    return await TryUpdateMyStatusGlobalOnce(status);  // function to retry
                };

                bool ok = await MembershipExecuteWithRetries(updateMyStatusTask, orleansConfig.Globals.MaxJoinAttemptTime);

                if (ok)
                {
                    if (logger.IsVerbose) logger.Verbose("-Silo {0} Successfully updated my Status in the Membership table to {1}", MyAddress.ToLongString(), status);
                    membershipOracleData.UpdateMyStatusLocal(status);
                    GossipMyStatus();
                }
                else
                {
                    errorString = String.Format("-Silo {0} failed to update its status to {1} in the Membership table due to write contention on the table after {2} attempts.",
                        MyAddress.ToLongString(), status, numCalls);
                    logger.Error(ErrorCode.MembershipFailedToWriteConditional, errorString);
                    throw new OrleansException(errorString);
                }
            }
            catch (Exception exc)
            {
                if (errorString == null)
                {
                    errorString = String.Format("-Silo {0} failed to update its status to {1} in the table due to failures (socket failures or table read/write failures) after {2} attempts: {3}", MyAddress.ToLongString(), status, numCalls, exc.Message);
                    logger.Error(ErrorCode.MembershipFailedToWrite, errorString);
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
            MembershipTableData table;
            if (newStatus == SiloStatus.Active)
            {
                table = await membershipTableProvider.ReadAll();
            }
            else
            {
                table = await membershipTableProvider.ReadRow(MyAddress);
            }

            if (logger.IsVerbose) logger.Verbose("-TryUpdateMyStatusGlobalOnce: Read{0} Membership table {1}", (newStatus.Equals(SiloStatus.Active) ? "All" : " my entry from"), table.ToString());
            CheckMissedIAmAlives(table);

            MembershipEntry myEntry;
            string myEtag = null;
            if (table.Contains(MyAddress))
            {
                var myTuple = table.Get(MyAddress);
                myEntry = myTuple.Item1;
                myEtag = myTuple.Item2;
                myEntry.TryUpdateStartTime(membershipOracleData.SiloStartTime);
                if (myEntry.Status == SiloStatus.Dead) // check if the table already knows that I am dead
                {
                    var msg = string.Format("I should be Dead according to membership table (in TryUpdateMyStatusGlobalOnce): myEntry = {0}.", myEntry.ToFullString());
                    logger.Warn(ErrorCode.MembershipFoundMyselfDead1, msg);
                    KillMyselfLocally(msg);
                    return true;
                }
            }
            else // first write attempt of this silo. Insert instead of Update.
            {
                myEntry = membershipOracleData.CreateNewMembershipEntry(nodeConfig, newStatus);
            }

            var now = DateTime.UtcNow;
            if (newStatus == SiloStatus.Dead)
                myEntry.AddSuspector(MyAddress, now); // add the killer (myself) to the suspect list, for easier diagnostics later on.
            
            myEntry.Status = newStatus;
            myEntry.IAmAliveTime = now;

            if (newStatus == SiloStatus.Active && orleansConfig.Globals.ValidateInitialConnectivity)
                await GetJoiningPreconditionPromise(table);
            
            TableVersion next = table.Version.Next();
            if (myEtag != null) // no previous etag for my entry -> its the first write to this entry, so insert instead of update.
                return await membershipTableProvider.UpdateRow(myEntry, myEtag, next);
            
            return await membershipTableProvider.InsertRow(myEntry, next);
        }

        private async Task GetJoiningPreconditionPromise(MembershipTableData table)
        {
            // send pings to all Active nodes, that are known to be alive
            List<MembershipEntry> members = table.Members.Select(tuple => tuple.Item1).Where(
                entry => entry.Status == SiloStatus.Active &&
                        !entry.SiloAddress.Equals(MyAddress) &&
                        !HasMissedIAmAlives(entry, false)).ToList();

            logger.LogWithoutBulkingAndTruncating(Severity.Info, ErrorCode.MembershipSendingPreJoinPing, "About to send pings to {0} nodes in order to validate communication in the Joining state. Pinged nodes = {1}",
                members.Count.ToString(), Utils.EnumerableToString(members, entry => entry.ToFullString(true)));

            var pingPromises = new List<Task>();
            foreach (var entry in members)
            {
                var siloCapture = entry.SiloAddress; // Capture loop variable
                int counterCapture = pingCounter++;

                // Send Pings as fan-out calls, so don't use await here
                pingPromises.Add(SendPing(siloCapture, counterCapture)
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Exception exc = task.Exception;
                            LogFailedProbe(siloCapture, counterCapture, exc);
                            throw exc;
                        }
                        return true;
                    }));
            }
            try
            {
                await Task.WhenAll(pingPromises);
            } catch (Exception)
            {
                logger.Error(ErrorCode.MembershipJoiningPreconditionFailure, 
                    String.Format("-Failed to get ping responses from all {0} silos that are currently listed as Active in the Membership table. " + 
                                    "Newly joining silos validate connectivity with all pre-existing silos that are listed as Active in the table " +
                                    "and have written I Am Alive in the table in the last {1} period, before they are allowed to join the cluster. Active silos are: {2}",
                        members.Count, AllowedIAmAliveMissPeriod, Utils.EnumerableToString(members, entry => entry.ToFullString(true))));
                throw;
            }
        }

        #endregion

        private async Task ProcessTableUpdate(MembershipTableData table, string caller, bool logAtInfoLevel = false)
        {
            if (logAtInfoLevel) logger.LogWithoutBulkingAndTruncating(Severity.Info, ErrorCode.MembershipReadAll_1, "-ReadAll (called from {0}) Membership table {1}", caller, table.ToString());
            else if (logger.IsVerbose) logger.Verbose("-ReadAll (called from {0}) Membership table {1}", caller, table.ToString());

            // Even if failed to clean up old entries from the table, still process the new entries. Will retry cleanup next time.
            try
            {
                await CleanupTableEntries(table);
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
                // just eat the exception.
            }

            bool localViewChanged = false;
            CheckMissedIAmAlives(table);
            // only process the table if in the active or ShuttingDown state. In other states I am not ready yet.
            if (IsFunctionalMBR(CurrentStatus))
            {
                foreach (var entry in table.Members.Select(tuple => tuple.Item1))
                {
                    if (!entry.SiloAddress.Endpoint.Equals(MyAddress.Endpoint))
                    {
                        bool changed = membershipOracleData.TryUpdateStatusAndNotify(entry);
                        localViewChanged = localViewChanged || changed;
                    }
                    else
                    {
                        membershipOracleData.UpdateMyFaultAndUpdateZone(entry);
                    }
                }

                if (localViewChanged)
                    UpdateListOfProbedSilos();
            }

            if (localViewChanged) logger.LogWithoutBulkingAndTruncating(Severity.Info, ErrorCode.MembershipReadAll_2,
                "-ReadAll (called from {0}, after local view changed, with removed duplicate deads) Membership table: {1}",
                caller, table.SupressDuplicateDeads().ToString());
        }

        private void CheckMissedIAmAlives(MembershipTableData table)
        {
            foreach (var entry in table.Members.Select(tuple => tuple.Item1).
                                                            Where(entry => !entry.SiloAddress.Equals(MyAddress)).
                                                            Where(entry => entry.Status == SiloStatus.Active))
            {
                HasMissedIAmAlives(entry, true);
            }
        }

        private bool HasMissedIAmAlives(MembershipEntry entry, bool writeWarning)
        {
            var now = LogFormatter.ParseDate(LogFormatter.PrintDate(DateTime.UtcNow));
            var lastIAmAlive = entry.IAmAliveTime;

            if (entry.IAmAliveTime.Equals(default(DateTime)))
                lastIAmAlive = entry.StartTime; // he has not written first IAmAlive yet, use its start time instead.

            if (now - lastIAmAlive <= AllowedIAmAliveMissPeriod) return false;

            if (writeWarning)
            {
                logger.Warn(ErrorCode.MembershipMissedIAmAliveTableUpdate,
                    String.Format("Noticed that silo {0} has not updated it's IAmAliveTime table column recently. Last update was at {1}, now is {2}, no update for {3}, which is more than {4}.",
                        entry.SiloAddress.ToLongString(),
                        lastIAmAlive,
                        now,
                        now - lastIAmAlive,
                        AllowedIAmAliveMissPeriod));
            }
            return true;
        }

        private async Task<bool> CleanupTableEntries(MembershipTableData table)
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
                        logger.Warn(ErrorCode.MembershipFoundMyselfDead2, msg);
                        KillMyselfLocally(msg);
                    }
                    continue;
                }
                
                if (entry.Status == SiloStatus.Dead)
                {
                    if (logger.IsVerbose2) logger.Verbose2("Skipping my previous old Dead entry in membership table: {0}", entry.ToFullString());
                    continue;
                }

                if (logger.IsVerbose) logger.Verbose("Temporal anomaly detected in membership table -- Me={0} Other me={1}",
                    MyAddress.ToLongString(), siloAddress.ToLongString());

                // Temporal paradox - There is an older clone of this silo in the membership table
                if (siloAddress.Generation < MyAddress.Generation)
                {
                    logger.Warn(ErrorCode.MembershipDetectedOlder, "Detected older version of myself - Marking other older clone as Dead -- Current Me={0} Older Me={1}, Old entry= {2}",
                        MyAddress.ToLongString(), siloAddress.ToLongString(), entry.ToFullString());
                    // Declare older clone of me as Dead.
                    silosToDeclareDead.Add(tuple);   //return DeclareDead(entry, eTag, tableVersion);
                }
                else if (siloAddress.Generation > MyAddress.Generation)
                {
                    // I am the older clone - Newer version of me should survive - I need to kill myself
                    var msg = string.Format("Detected newer version of myself - I am the older clone so will commit suicide -- Current Me={0} Newer Me={1}, Current entry= {2}",
                        MyAddress.ToLongString(), siloAddress.ToLongString(), entry.ToFullString());
                    logger.Warn(ErrorCode.MembershipDetectedNewer, msg);
                    await KillMyself();
                    KillMyselfLocally(msg);
                    return true; // No point continuing!
                }

            }

            if (silosToDeclareDead.Count == 0) return true;

            if (logger.IsVerbose) logger.Verbose("CleanupTableEntries: About to DeclareDead {0} outdated silos in the table: {1}", silosToDeclareDead.Count,
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
            var msg = "I have been told I am dead, so this silo will commit suicide! " + reason;
            logger.Error(ErrorCode.MembershipKillMyselfLocally, msg);
            bool alreadyStopping = CurrentStatus.IsTerminating();

            DisposeTimers();
            membershipOracleData.UpdateMyStatusLocal(SiloStatus.Dead);

            if (!alreadyStopping || !orleansConfig.IsRunningAsUnitTest)
            {
                logger.Fail(ErrorCode.MembershipKillMyselfLocally, msg);
            }
            // do not abort in unit tests.
        }

        private void GossipMyStatus()
        {
            GossipToOthers(MyAddress, CurrentStatus);
        }

        private void GossipToOthers(SiloAddress updatedSilo, SiloStatus updatedStatus)
        {
            if (!orleansConfig.Globals.UseLivenessGossip) return;

            // spread the rumor that some silo has just been marked dead
            foreach (var silo in membershipOracleData.GetSiloStatuses(IsFunctionalMBR, false).Keys)
            {
                if (logger.IsVerbose2) logger.Verbose2("-Sending status update GOSSIP notification about silo {0}, status {1}, to silo {2}", updatedSilo.ToLongString(), updatedStatus, silo.ToLongString());
                GetOracleReference(silo)
                    .SiloStatusChangeNotification(updatedSilo, updatedStatus)
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Exception exc = task.Exception;
                            logger.Error(ErrorCode.MembershipGossipSendFailure, "SiloStatusChangeNotification failed", exc);
                            throw exc;
                        }
                        return true;
                    })
                    .Ignore();
            }
        }

        private void UpdateListOfProbedSilos()
        {
            // if I am still not fully functional, I should not be probing others.
            if (!IsFunctionalMBR(CurrentStatus)) return;

            // keep watching shutting-down silos as well, so we can properly ensure they are dead.
            List<SiloAddress> tmpList = membershipOracleData.GetSiloStatuses(IsFunctionalMBR, true).Keys.ToList();

            tmpList.Sort((x, y) => x.GetConsistentHashCode().CompareTo(y.GetConsistentHashCode()));

            int myIndex = tmpList.FindIndex(el => el.Equals(MyAddress));
            if (myIndex < 0)
            {
                // this should not happen ...
                var error = String.Format("This silo {0} status {1} is not in its own local silo list! This is a bug!", MyAddress.ToLongString(), CurrentStatus);
                logger.Error(ErrorCode.Runtime_Error_100305, error);
                throw new Exception(error);
            }

            // Go over every node excluding me,
            // Find up to NumProbedSilos silos after me, which are not suspected by anyone and add them to the probedSilos,
            // In addition, every suspected silo you encounter on the way, add him to the probedSilos.
            var silosToWatch = new List<SiloAddress>();
            var additionalSilos = new List<SiloAddress>();

            for (int i = 0; i < tmpList.Count - 1 && silosToWatch.Count < orleansConfig.Globals.NumProbedSilos; i++)
            {
                SiloAddress candidate = tmpList[(myIndex + i + 1) % tmpList.Count];
                bool isSuspected = membershipOracleData.GetSiloEntry(candidate).GetFreshVotes(orleansConfig.Globals.DeathVoteExpirationTimeout).Count > 0;
                if (isSuspected)
                {
                    additionalSilos.Add(candidate);
                }
                else
                {
                    silosToWatch.Add(candidate);
                }
            }

            // take new watched silos, but leave the probe counters for the old ones.
            var newProbedSilos = new Dictionary<SiloAddress, int>();
            foreach (var silo in silosToWatch.Union(additionalSilos))
            {
                int oldValue;
                probedSilos.TryGetValue(silo, out oldValue);
                if (!newProbedSilos.ContainsKey(silo)) // duplication suppression.
                    newProbedSilos[silo] = oldValue;
            }

            if (!AreTheSame(probedSilos.Keys, newProbedSilos.Keys))
            {
                logger.LogWithoutBulkingAndTruncating(Severity.Info, ErrorCode.MembershipWatchList, "Will watch (actively ping) {0} silos: {1}",
                    newProbedSilos.Count, Utils.EnumerableToString(newProbedSilos.Keys, silo => silo.ToLongString()));
            }

            probedSilos = newProbedSilos;
        }

        private static bool AreTheSame<T>(ICollection<T> first, ICollection<T> second)
        {
            int count = first.Count;
            if (count != second.Count) return false;
            return first.Intersect(second).Count() == count;
        }

        private void OnGetTableUpdateTimer(object data)
        {
            if (logger.IsVerbose2) logger.Verbose2("-{0} fired {1}. CurrentStatus {2}", timerGetTableUpdates.Name, timerGetTableUpdates.GetNumTicks(), CurrentStatus);

            timerGetTableUpdates.CheckTimerDelay();

            Task<MembershipTableData> tablePromise = membershipTableProvider.ReadAll();
            tablePromise.ContinueWith(async task =>
            {
                try
                {
                    MembershipTableData table = await task; // Force Ex ception to be thrown if IsFaulted
                    await ProcessTableUpdate(table, "timer");
                    return true;
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.MembershipTimerProcessingFailure, "ProcessTableUpdate failed", exc);
                    throw;
                }
            }).Unwrap().Ignore();
        }

        private void OnProbeOtherSilosTimer(object data)
        {
            if (logger.IsVerbose2) logger.Verbose2("-{0} fired {1}. CurrentStatus {2}", timerProbeOtherSilos.Name, timerProbeOtherSilos.GetNumTicks(), CurrentStatus);

            timerProbeOtherSilos.CheckTimerDelay();

            List<SiloAddress> silos = probedSilos.Keys.ToList(); // Take working copy
            foreach (var silo in silos)
            {
                var siloAddress = silo; // Capture loop variable
                int counterCapture = pingCounter++;
                try
                {
                    // Send Pings as fan-out calls, so don't use await here
                    SendPing(siloAddress, counterCapture)
                        .ContinueWith(task =>
                        {
                            try
                            {
                                if (task.IsFaulted)
                                {
                                    Exception exc = task.Exception;
                                    // Ping failed
                                    IncFailedProbes(siloAddress, counterCapture, exc);
                                    return false;
                                }
                                // Ping was successfull
                                ResetFailedProbes(siloAddress, counterCapture);
                                return true;
                            }
                            catch (Exception exc)
                            {
                                logger.Error(ErrorCode.MembershipSendPingFailure, "Handle probe responses failed", exc);
                                throw;
                            }
                        }).Ignore();
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.MembershipSendPingFailure, "SendPing failed", exc);
                    throw;
                }
            }
        }

        private void OnIAmAliveUpdateInTableTimer(object data)
        {
            if (logger.IsVerbose2) logger.Verbose2("-{0} fired {1}. CurrentStatus {2}", timerIAmAliveUpdateInTable.Name, timerIAmAliveUpdateInTable.GetNumTicks(), CurrentStatus);

            timerIAmAliveUpdateInTable.CheckTimerDelay();

            var entry = new MembershipEntry
            {
                SiloAddress = MyAddress,
                IAmAliveTime = DateTime.UtcNow
            };

            membershipTableProvider.UpdateIAmAlive(entry)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        Exception exc = task.Exception;
                        logger.Error(ErrorCode.MembershipUpdateIAmAliveFailure, "UpdateIAmAlive failed", exc);
                        throw exc;
                    }
                    return true;
                }).Ignore();
        }

        private Task SendPing(SiloAddress siloAddress, int pingNumber)
        {
            if (logger.IsVerbose2) logger.Verbose2("-Going to send Ping #{0} to probe silo {1}", pingNumber, siloAddress.ToLongString());
            Task pingTask;
            try
            {
                RequestContext.Set(RequestContext.PING_APPLICATION_HEADER, true);
                pingTask = GetOracleReference(siloAddress).Ping(pingNumber);

                // Update stats counters -- only count Pings that were successfuly sent [but not necessarily replied to]
                MessagingStatisticsGroup.OnPingSend(siloAddress);
            }
            finally
            {
                RequestContext.Remove(RequestContext.PING_APPLICATION_HEADER);
            }
            return pingTask;
        }

        private void ResetFailedProbes(SiloAddress silo, int pingNumber)
        {
            if (logger.IsVerbose2) logger.Verbose2("-Got successful ping response for ping #{0} from {1}", pingNumber, silo.ToLongString());
            MessagingStatisticsGroup.OnPingReplyReceived(silo);
            if (probedSilos.ContainsKey(silo))
            {
                // need this check to avoid races with changed membership; 
                // otherwise, we might insert here a new entry to the 'probedSilos' dictionary
                probedSilos[silo] = 0;
            }
        }

        private void IncFailedProbes(SiloAddress silo, int pingNumber, Exception failureReason)
        {
            MessagingStatisticsGroup.OnPingReplyMissed(silo);
            if (!probedSilos.ContainsKey(silo))
            {
                // need this check to avoid races with changed membership (I was watching him, but then read the table, learned he is already dead and thus no longer wtaching him); 
                // otherwise, we might here insert a new entry to the 'probedSilos' dictionary
                logger.Info(ErrorCode.MembershipPingedSiloNotInWatchList, "-Does not have {0} in the list of probes, ignoring", silo.ToLongString());
                return;
            }

            LogFailedProbe(silo, pingNumber, failureReason);

            probedSilos[silo] = probedSilos[silo] + 1;

            if (logger.IsVerbose2) logger.Verbose2("-Current number of failed probes for {0}: {1}", silo.ToLongString(), probedSilos[silo]);
            if (probedSilos[silo] < orleansConfig.Globals.NumMissedProbesLimit)
                return;
            
            MembershipExecuteWithRetries(
                _ => TryToSuspectOrKill(silo), orleansConfig.Globals.MaxJoinAttemptTime)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        Exception exc = task.Exception;
                        logger.Error(ErrorCode.MembershipFailedToSuspect, "TryToSuspectOrKill failed", exc);
                        throw exc;
                    }
                    return true;
                }).Ignore();
        }

        private void LogFailedProbe(SiloAddress silo, int pingNumber, Exception failureReason)
        {
            var reason = String.Format("Original Exc Type: {0} Message:{1}", failureReason.GetBaseException().GetType(), failureReason.GetBaseException().Message);
            logger.Warn(ErrorCode.MembershipMissedPing, "-Did not get ping response for ping #{0} from {1}. Reason = {2}", pingNumber, silo.ToLongString(), reason);
        }

        private async Task<bool> TryToSuspectOrKill(SiloAddress silo)
        {
            MembershipTableData table = await membershipTableProvider.ReadAll();

            if (logger.IsVerbose) logger.Verbose("-TryToSuspectOrKill: Read Membership table {0}", table.ToString());
            if (table.Contains(MyAddress))
            {
                var myEntry = table.Get(MyAddress).Item1;
                if (myEntry.Status == SiloStatus.Dead) // check if the table already knows that I am dead
                {
                    var msg = string.Format("I should be Dead according to membership table (in TryToSuspectOrKill): entry = {0}.", myEntry.ToFullString());
                    logger.Warn(ErrorCode.MembershipFoundMyselfDead3, msg);
                    KillMyselfLocally(msg);
                    return true;
                }
            }

            if (!table.Contains(silo))
            {
                // this should not happen ...
                var str = String.Format("-Could not find silo entry for silo {0} in the table.", silo.ToLongString());
                logger.Error(ErrorCode.MembershipFailedToReadSilo, str);
                throw new KeyNotFoundException(str);
            }

            var tuple = table.Get(silo);
            var entry = tuple.Item1;
            string eTag = tuple.Item2;
            if (logger.IsVerbose) logger.Verbose("-TryToSuspectOrKill {0}: The current status of {0} in the table is {1}, its entry is {2}", entry.SiloAddress.ToLongString(), entry.Status, entry.ToFullString());
            // check if the table already knows that this silo is dead
            if (entry.Status == SiloStatus.Dead)
            {
                // try update our local table and notify
                bool changed = membershipOracleData.TryUpdateStatusAndNotify(entry);
                if (changed)
                    UpdateListOfProbedSilos();
                
                return true;
            }

            var allVotes = entry.SuspectTimes ?? new List<Tuple<SiloAddress, DateTime>>();

            // get all valid (non-expired) votes
            var freshVotes = entry.GetFreshVotes(orleansConfig.Globals.DeathVoteExpirationTimeout);

            if (logger.IsVerbose2) logger.Verbose2("-Current number of fresh Voters for {0} is {1}", silo.ToLongString(), freshVotes.Count.ToString());

            if (freshVotes.Count >= orleansConfig.Globals.NumVotesForDeathDeclaration)
            {
                // this should not happen ...
                var str = String.Format("-Silo {0} is suspected by {1} which is more or equal than {2}, but is not marked as dead. This is a bug!!!",
                    entry.SiloAddress.ToLongString(), freshVotes.Count.ToString(), orleansConfig.Globals.NumVotesForDeathDeclaration.ToString());
                logger.Error(ErrorCode.Runtime_Error_100053, str);
                KillMyselfLocally("Found a bug 1! Will commit suicide.");
                return false;
            }

            // handle the corner case when the number of active silos is very small (then my only vote is enough)
            int activeSilos = membershipOracleData.GetSiloStatuses(status => status == SiloStatus.Active, true).Count;
            // find if I have already voted
            int myVoteIndex = freshVotes.FindIndex(voter => MyAddress.Equals(voter.Item1));

            // Try to kill:
            //  if there is NumVotesForDeathDeclaration votes (including me) to kill - kill.
            //  otherwise, if there is a majority of nodes (including me) voting to kill – kill.
            bool declareDead = false;
            int myAdditionalVote = myVoteIndex == -1 ? 1 : 0;

            if (freshVotes.Count + myAdditionalVote >= orleansConfig.Globals.NumVotesForDeathDeclaration)
                declareDead = true;
            
            if (freshVotes.Count + myAdditionalVote >= (activeSilos + 1) / 2)
                declareDead = true;
            
            if (declareDead)
            {
                // kick this silo off
                logger.Info(ErrorCode.MembershipMarkingAsDead, 
                    "-Going to mark silo {0} as DEAD in the table #1. I am the last voter: #freshVotes={1}, myVoteIndex = {2}, NumVotesForDeathDeclaration={3} , #activeSilos={4}, suspect list={5}",
                            entry.SiloAddress.ToLongString(), 
                            freshVotes.Count, 
                            myVoteIndex, 
                            orleansConfig.Globals.NumVotesForDeathDeclaration, 
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
                if (allVotes.Count >= orleansConfig.Globals.NumVotesForDeathDeclaration) // if the list is full
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
            logger.Info(ErrorCode.MembershipVotingForKill,
                "-Putting my vote to mark silo {0} as DEAD #2. Previous suspect list is {1}, trying to update to {2}, eTag={3}, freshVotes is {4}",
                entry.SiloAddress.ToLongString(), 
                PrintSuspectList(prevList), 
                PrintSuspectList(entry.SuspectTimes),
                eTag,
                PrintSuspectList(freshVotes));

            // If we fail to update here we will retry later.
            return await membershipTableProvider.UpdateRow(entry, eTag, table.Version.Next());
        }

        private async Task<bool> DeclareDead(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            if (orleansConfig.Globals.LivenessEnabled)
            {
                // add the killer (myself) to the suspect list, for easier diagnosis later on.
                entry.AddSuspector(MyAddress, DateTime.UtcNow);

                if (logger.IsVerbose) logger.Verbose("-Going to DeclareDead silo {0} in the table. About to write entry {1}.", entry.SiloAddress.ToLongString(), entry.ToFullString());
                entry.Status = SiloStatus.Dead;
                bool ok = await membershipTableProvider.UpdateRow(entry, etag, tableVersion.Next());
                if (ok)
                {
                    if (logger.IsVerbose) logger.Verbose("-Successfully updated {0} status to Dead in the Membership table.", entry.SiloAddress.ToLongString());
                    if (!entry.SiloAddress.Endpoint.Equals(MyAddress.Endpoint))
                    {
                        bool changed = membershipOracleData.TryUpdateStatusAndNotify(entry);
                        if (changed)
                            UpdateListOfProbedSilos();
                        
                    }

                    GossipToOthers(entry.SiloAddress, entry.Status);
                    return true;
                }
                
                logger.Info(ErrorCode.MembershipMarkDeadWriteFailed, "-Failed to update {0} status to Dead in the Membership table, due to write conflicts. Will retry.", entry.SiloAddress.ToLongString());
                return false;
            }
            
            logger.Info(ErrorCode.MembershipCantWriteLivenessDisabled, "-Want to mark silo {0} as DEAD, but will ignore because Liveness is Disabled.", entry.SiloAddress.ToLongString());
            return true;
        }

        private static string PrintSuspectList(IEnumerable<Tuple<SiloAddress, DateTime>> list)
        {
            return Utils.EnumerableToString(list, t => String.Format("<{0}, {1}>", 
                t.Item1.ToLongString(), LogFormatter.PrintDate(t.Item2)));
        }

        private void DisposeTimers()
        {
            if (timerGetTableUpdates != null)
            {
                timerGetTableUpdates.Dispose();
                timerGetTableUpdates = null;
            }
            if (timerProbeOtherSilos != null)
            {
                timerProbeOtherSilos.Dispose();
                timerProbeOtherSilos = null;
            }
            if (timerIAmAliveUpdateInTable != null)
            {
                timerIAmAliveUpdateInTable.Dispose();
                timerIAmAliveUpdateInTable = null;
            }
        }

        #region Implementation of IHealthCheckParticipant

        public bool CheckHealth(DateTime lastCheckTime)
        {
            bool ok = (timerGetTableUpdates != null) && timerGetTableUpdates.CheckTimerFreeze(lastCheckTime);
            ok &= (timerProbeOtherSilos != null) && timerProbeOtherSilos.CheckTimerFreeze(lastCheckTime);
            ok &= (timerIAmAliveUpdateInTable != null) && timerIAmAliveUpdateInTable.CheckTimerFreeze(lastCheckTime);
            return ok;
        }

        #endregion

        private static IMembershipService GetOracleReference(SiloAddress silo)
        {
            return InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IMembershipService>(Constants.MembershipOracleId, silo);
        }
    }
}
