using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.ReminderService
{
    internal class LocalReminderService : GrainService, IReminderService
    {
        private const int InitialReadRetryCountBeforeFastFailForUpdates = 2;
        private static readonly TimeSpan InitialReadMaxWaitTimeForUpdates = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan InitialReadRetryPeriod = TimeSpan.FromSeconds(30);

        private readonly Dictionary<ReminderIdentity, LocalReminderData> localReminders;
        private readonly IReminderTable reminderTable;
        private long localTableSequence;
        private IGrainTimer listRefreshTimer; // timer that refreshes our list of reminders to reflect global reminder table
        private readonly TaskCompletionSource<bool> startedTask;
        private uint initialReadCallCount = 0;

        private readonly AverageTimeSpanStatistic tardinessStat;
        private readonly CounterStatistic ticksDeliveredStat;
        private readonly GlobalConfiguration config;
        private readonly TimeSpan initTimeout;

        internal LocalReminderService(
            Silo silo,
            GrainId id,
            IReminderTable reminderTable,
            GlobalConfiguration config,
            TimeSpan initTimeout)
            : base(id, silo, null)
        {
            localReminders = new Dictionary<ReminderIdentity, LocalReminderData>();
            this.reminderTable = reminderTable;
            this.config = config;
            this.initTimeout = initTimeout;
            localTableSequence = 0;
            tardinessStat = AverageTimeSpanStatistic.FindOrCreate(StatisticNames.REMINDERS_AVERAGE_TARDINESS_SECONDS);
            IntValueStatistic.FindOrCreate(StatisticNames.REMINDERS_NUMBER_ACTIVE_REMINDERS, () => localReminders.Count);
            ticksDeliveredStat = CounterStatistic.FindOrCreate(StatisticNames.REMINDERS_COUNTERS_TICKS_DELIVERED);
            startedTask = new TaskCompletionSource<bool>();
        }

        #region Public methods

        /// <summary>
        /// Attempt to retrieve reminders, that are my responsibility, from the global reminder table when starting this silo (reminder service instance)
        /// </summary>
        /// <returns></returns>
        public override async Task Start()
        {
            // confirm that it can access the underlying store, as after this the ReminderService will load in the background, without the opportunity to prevent the Silo from starting
            await reminderTable.Init(config, Logger).WithTimeout(initTimeout);

            await base.Start();
        }

        public async override Task Stop()
        {
            await base.Stop();

            if (listRefreshTimer != null)
            {
                listRefreshTimer.Dispose();
                listRefreshTimer = null;
            }
            foreach (LocalReminderData r in localReminders.Values)
                r.StopReminder(Logger);

            // for a graceful shutdown, also handover reminder responsibilities to new owner, and update the ReminderTable
            // currently, this is taken care of by periodically reading the reminder table
        }

        public async Task<IGrainReminder> RegisterOrUpdateReminder(GrainReference grainRef, string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            var entry = new ReminderEntry
            {
                GrainRef = grainRef,
                ReminderName = reminderName,
                StartAt = DateTime.UtcNow.Add(dueTime),
                Period = period,
            };

            if(Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_RegisterOrUpdate, "RegisterOrUpdateReminder: {0}", entry.ToString());
            await DoResponsibilitySanityCheck(grainRef, "RegisterReminder");
            var newEtag = await reminderTable.UpsertRow(entry);

            if (newEtag != null)
            {
                if (Logger.IsVerbose) Logger.Verbose("Registered reminder {0} in table, assigned localSequence {1}", entry, localTableSequence);
                entry.ETag = newEtag;
                StartAndAddTimer(entry);
                if (Logger.IsVerbose3) PrintReminders();
                return new ReminderData(grainRef, reminderName, newEtag) as IGrainReminder;
            }

            var msg = string.Format("Could not register reminder {0} to reminder table due to a race. Please try again later.", entry);
            Logger.Error(ErrorCode.RS_Register_TableError, msg);
            throw new ReminderException(msg);
        }

        /// <summary>
        /// Stop the reminder locally, and remove it from the external storage system
        /// </summary>
        /// <param name="reminder"></param>
        /// <returns></returns>
        public async Task UnregisterReminder(IGrainReminder reminder)
        {
            var remData = (ReminderData)reminder;
            if(Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_Unregister, "UnregisterReminder: {0}, LocalTableSequence: {1}", remData, localTableSequence);

            GrainReference grainRef = remData.GrainRef;
            string reminderName = remData.ReminderName;
            string eTag = remData.ETag;

            await DoResponsibilitySanityCheck(grainRef, "RemoveReminder");

            // it may happen that we dont have this reminder locally ... even then, we attempt to remove the reminder from the reminder
            // table ... the periodic mechanism will stop this reminder at any silo's LocalReminderService that might have this reminder locally

            // remove from persistent/memory store
            var success = await reminderTable.RemoveRow(grainRef, reminderName, eTag);
            if (success)
            {
                bool removed = TryStopPreviousTimer(grainRef, reminderName);
                if (removed)
                {
                    if(Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_Stop, "Stopped reminder {0}", reminder);
                    if (Logger.IsVerbose3) PrintReminders(string.Format("After removing {0}.", reminder));
                }
                else
                {
                    // no-op
                    if(Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_RemoveFromTable, "Removed reminder from table which I didn't have locally: {0}.", reminder);
                }
            }
            else
            {
                var msg = string.Format("Could not unregister reminder {0} from the reminder table, due to tag mismatch. You can retry.", reminder);
                Logger.Error(ErrorCode.RS_Unregister_TableError, msg);
                throw new ReminderException(msg);
            }
        }

        public async Task<IGrainReminder> GetReminder(GrainReference grainRef, string reminderName)
        {
            if(Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_GetReminder,"GetReminder: GrainReference={0} ReminderName={1}", grainRef.ToDetailedString(), reminderName);
            var entry = await reminderTable.ReadRow(grainRef, reminderName);
            return entry == null ? null : entry.ToIGrainReminder();
        }

        public async Task<List<IGrainReminder>> GetReminders(GrainReference grainRef)
        {
            if (Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_GetReminders, "GetReminders: GrainReference={0}", grainRef.ToDetailedString());
            var tableData = await reminderTable.ReadRows(grainRef);
            return tableData.Reminders.Select(entry => entry.ToIGrainReminder()).ToList();
        }

        #endregion

        /// <summary>
        /// Attempt to retrieve reminders from the global reminder table
        /// </summary>
        private async Task ReadAndUpdateReminders()
        {
            if (StoppedCancellationTokenSource.IsCancellationRequested) return;

            RemoveOutOfRangeReminders();

            // try to retrieve reminders from all my subranges
            var rangeSerialNumberCopy = RangeSerialNumber;
            if (Logger.IsVerbose2) Logger.Verbose2($"My range= {RingRange}, RangeSerialNumber {RangeSerialNumber}. Local reminders count {localReminders.Count}");
            var acks = new List<Task>();
            foreach (var range in RangeFactory.GetSubRanges(RingRange))
            {
                acks.Add(ReadTableAndStartTimers(range, rangeSerialNumberCopy));
            }
            await Task.WhenAll(acks);
            if (Logger.IsVerbose3) PrintReminders();
        }

        private void RemoveOutOfRangeReminders()
        {
            var remindersOutOfRange = localReminders.Where(r => !RingRange.InRange(r.Key.GrainRef)).Select(r => r.Value).ToArray();
            
            foreach (var reminder in remindersOutOfRange)
            {
                if (Logger.IsVerbose2)
                    Logger.Verbose2("Not in my range anymore, so removing. {0}", reminder);
                // remove locally
                reminder.StopReminder(Logger);
                localReminders.Remove(reminder.Identity);
            }

            if (Logger.IsInfo && remindersOutOfRange.Length > 0) Logger.Info($"Removed {remindersOutOfRange.Length} local reminders that are now out of my range.");
        }

        #region Change in membership, e.g., failure of predecessor
        public override async Task OnRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            await base.OnRangeChange(oldRange, newRange, increased);
            if (Status == GrainServiceStatus.Started)
                await ReadAndUpdateReminders();
            else
                if (Logger.IsVerbose) Logger.Verbose("Ignoring range change until ReminderService is Started -- Current status = {0}", Status);
        }
        #endregion

        #region Internal implementation methods

        protected override async Task StartInBackground()
        {
            await DoInitialReadAndUpdateReminders();
            if (Status == GrainServiceStatus.Booting)
            {
                var random = new SafeRandom();
                listRefreshTimer = GrainTimer.FromTaskCallback(
                    _ => DoInitialReadAndUpdateReminders(),
                    null,
                    random.NextTimeSpan(InitialReadRetryPeriod),
                    InitialReadRetryPeriod,
                    name: "ReminderService.ReminderListInitialRead");
                listRefreshTimer.Start();
            }
        }

        private void PromoteToStarted()
        {
            if (StoppedCancellationTokenSource.IsCancellationRequested) return;

           // Logger.Info(ErrorCode.RS_ServiceStarted, "Reminder system target started OK on: {0} x{1,8:X8}, with range {2}", this.Silo, this.Silo.GetConsistentHashCode(), this.myRange);

            var random = new SafeRandom();
            var dueTime = random.NextTimeSpan(Constants.RefreshReminderList);
            if (listRefreshTimer != null) listRefreshTimer.Dispose();
            listRefreshTimer = GrainTimer.FromTaskCallback(
                _ => ReadAndUpdateReminders(),
                null,
                dueTime,
                Constants.RefreshReminderList,
                name: "ReminderService.ReminderListRefresher");
            listRefreshTimer.Start();
            Status = GrainServiceStatus.Started;
            startedTask.TrySetResult(true);
        }

        private async Task DoInitialReadAndUpdateReminders()
        {
            try
            {
                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                initialReadCallCount++;
                await this.ReadAndUpdateReminders();
                PromoteToStarted();
            }
            catch (Exception ex)
            {
                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                if (initialReadCallCount <= InitialReadRetryCountBeforeFastFailForUpdates)
                {
                    Logger.Warn(
                        ErrorCode.RS_ServiceInitialLoadFailing,
                        string.Format("ReminderService failed initial load of reminders and will retry. Attempt #{0}", this.initialReadCallCount),
                        ex);
                }
                else
                {
                    const string baseErrorMsg = "ReminderService failed initial load of reminders and cannot guarantee that the service will be eventually start without manual intervention or restarting the silo.";
                    var logErrorMessage = string.Format(baseErrorMsg + " Attempt #{0}", this.initialReadCallCount);
                    Logger.Error(ErrorCode.RS_ServiceInitialLoadFailed, logErrorMessage, ex);
                    startedTask.TrySetException(new OrleansException(baseErrorMsg, ex));
                }
            }
        }

        private async Task ReadTableAndStartTimers(IRingRange range, int rangeSerialNumberCopy)
        {
            if (Logger.IsVerbose) Logger.Verbose("Reading rows from {0}", range.ToString());
            localTableSequence++;
            long cachedSequence = localTableSequence;

            try
            {
                var srange = range as ISingleRange;
                if (srange == null)
                    throw new InvalidOperationException("LocalReminderService must be dealing with SingleRange");

                ReminderTableData table = await reminderTable.ReadRows(srange.Begin, srange.End); // get all reminders, even the ones we already have

                if (rangeSerialNumberCopy < RangeSerialNumber)
                {
                    if (Logger.IsVerbose) Logger.Verbose($"My range changed while reading from the table, ignoring the results. Another read has been started. RangeSerialNumber {RangeSerialNumber}, RangeSerialNumberCopy {rangeSerialNumberCopy}.");
                    return;
                }
                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                // if null is a valid value, it means that there's nothing to do.
                if (null == table && reminderTable is MockReminderTable) return;

                var remindersNotInTable = localReminders.Where(r => range.InRange(r.Key.GrainRef)).ToDictionary(r => r.Key, r => r.Value); // shallow copy
                if (Logger.IsVerbose) Logger.Verbose("For range {0}, I read in {1} reminders from table. LocalTableSequence {2}, CachedSequence {3}", range.ToString(), table.Reminders.Count, localTableSequence, cachedSequence);

                foreach (ReminderEntry entry in table.Reminders)
                {
                    var key = new ReminderIdentity(entry.GrainRef, entry.ReminderName);
                    LocalReminderData localRem;
                    if (localReminders.TryGetValue(key, out localRem))
                    {
                        if (cachedSequence > localRem.LocalSequenceNumber) // info read from table is same or newer than local info
                        {
                            if (localRem.Timer != null) // if ticking
                            {
                                if (Logger.IsVerbose2) Logger.Verbose2("In table, In local, Old, & Ticking {0}", localRem);
                                // it might happen that our local reminder is different than the one in the table, i.e., eTag is different
                                // if so, stop the local timer for the old reminder, and start again with new info
                                if (!localRem.ETag.Equals(entry.ETag))
                                // this reminder needs a restart
                                {
                                    if (Logger.IsVerbose2) Logger.Verbose2("{0} Needs a restart", localRem);
                                    localRem.StopReminder(Logger);
                                    localReminders.Remove(localRem.Identity);
                                    StartAndAddTimer(entry);
                                }
                            }
                            else // if not ticking
                            {
                                // no-op
                                if (Logger.IsVerbose2) Logger.Verbose2("In table, In local, Old, & Not Ticking {0}", localRem);
                            }
                        }
                        else // cachedSequence < localRem.LocalSequenceNumber ... // info read from table is older than local info
                        {
                            if (localRem.Timer != null) // if ticking
                            {
                                // no-op
                                if (Logger.IsVerbose2) Logger.Verbose2("In table, In local, Newer, & Ticking {0}", localRem);
                            }
                            else // if not ticking
                            {
                                // no-op
                                if (Logger.IsVerbose2) Logger.Verbose2("In table, In local, Newer, & Not Ticking {0}", localRem);
                            }
                        }
                    }
                    else // exists in table, but not locally
                    {
                        if (Logger.IsVerbose2) Logger.Verbose2("In table, Not in local, {0}", entry);
                        // create and start the reminder
                        StartAndAddTimer(entry);
                    }
                    // keep a track of extra reminders ... this 'reminder' is useful, so remove it from extra list
                    remindersNotInTable.Remove(key);
                } // foreach reminder read from table

                int remindersCountBeforeRemove = localReminders.Count;
                
                // foreach reminder that is not in global table, but exists locally
                foreach (var reminder in remindersNotInTable.Values)
                {
                    if (cachedSequence < reminder.LocalSequenceNumber)
                    {
                        // no-op
                        if (Logger.IsVerbose2) Logger.Verbose2("Not in table, In local, Newer, {0}", reminder);
                    }
                    else // cachedSequence > reminder.LocalSequenceNumber
                    {
                        if (Logger.IsVerbose2) Logger.Verbose2("Not in table, In local, Old, so removing. {0}", reminder);
                        // remove locally
                        reminder.StopReminder(Logger);
                        localReminders.Remove(reminder.Identity);
                    }
                }
                if (Logger.IsVerbose) Logger.Verbose($"Removed {localReminders.Count - remindersCountBeforeRemove} reminders from local table");
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.RS_FailedToReadTableAndStartTimer, "Failed to read rows from table.", exc);
                throw;
            }
        }

        private void StartAndAddTimer(ReminderEntry entry)
        {
            // it might happen that we already have a local reminder with a different eTag
            // if so, stop the local timer for the old reminder, and start again with new info
            // Note: it can happen here that we restart a reminder that has the same eTag as what we just registered ... its a rare case, and restarting it doesn't hurt, so we don't check for it
            var key = new ReminderIdentity(entry.GrainRef, entry.ReminderName);
            LocalReminderData prevReminder;
            if (localReminders.TryGetValue(key, out prevReminder)) // if found locally
            {
                if (Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_LocalStop, "Localy stopping reminder {0} as it is different than newly registered reminder {1}", prevReminder, entry);
                prevReminder.StopReminder(Logger);
                localReminders.Remove(prevReminder.Identity);
            }

            var newReminder = new LocalReminderData(entry);
            localTableSequence++;
            newReminder.LocalSequenceNumber = localTableSequence;
            localReminders.Add(newReminder.Identity, newReminder);
            newReminder.StartTimer(AsyncTimerCallback, Logger);
            if (Logger.IsVerbose) Logger.Verbose(ErrorCode.RS_Started, "Started reminder {0}.", entry.ToString());
        }

        // stop without removing it. will remove later.
        private bool TryStopPreviousTimer(GrainReference grainRef, string reminderName)
        {
            // we stop the locally running timer for this reminder
            var key = new ReminderIdentity(grainRef, reminderName);
            LocalReminderData localRem;
            if (!localReminders.TryGetValue(key, out localRem)) return false;

            // if we have it locally
            localTableSequence++; // move to next sequence
            localRem.LocalSequenceNumber = localTableSequence;
            localRem.StopReminder(Logger);
            return true;
        }

        #endregion

        /// <summary>
        /// Local timer expired ... notify it as a 'tick' to the grain who registered this reminder
        /// </summary>
        /// <param name="rem">Reminder that this timeout represents</param>
        private async Task AsyncTimerCallback(object rem)
        {
            var reminder = (LocalReminderData)rem;

            if (!localReminders.ContainsKey(reminder.Identity) // we have already stopped this timer
                || reminder.Timer == null) // this timer was unregistered, and is waiting to be gc-ied
                return;

            ticksDeliveredStat.Increment();
            await reminder.OnTimerTick(tardinessStat, Logger);
        }

        #region Utility (less useful) methods

        private async Task DoResponsibilitySanityCheck(GrainReference grainRef, string debugInfo)
        {
            switch (Status)
            {
                case GrainServiceStatus.Booting:
                    // if service didn't finish the initial load, it could still be loading normally or it might have already 
                    // failed a few attempts and callers should not be hold waiting for it to complete
                    var task = this.startedTask.Task;
                    if (task.IsCompleted)
                    {
                        // task at this point is already Faulted
                        await task;
                    }
                    else
                    {
                        try
                        {
                            // wait for the initial load task to complete (with a timeout)
                            await task.WithTimeout(InitialReadMaxWaitTimeForUpdates);
                        }
                        catch (TimeoutException ex)
                        {
                            throw new OrleansException("Reminder Service is still initializing and it is taking a long time. Please retry again later.", ex);
                        }
                    }
                    break;
                case GrainServiceStatus.Started:
                    break;
                case GrainServiceStatus.Stopped:
                    throw new OperationCanceledException("ReminderService has been stopped.");
                default:
                    throw new InvalidOperationException("status");
            }

            if (!RingRange.InRange(grainRef))
            {
                Logger.Warn(ErrorCode.RS_NotResponsible, "I shouldn't have received request '{0}' for {1}. It is not in my responsibility range: {2}",
                    debugInfo, grainRef.ToDetailedString(), RingRange);
                // For now, we still let the caller proceed without throwing an exception... the periodical mechanism will take care of reminders being registered at the wrong silo
                // otherwise, we can either reject the request, or re-route the request
            }
        }

        // Note: The list of reminders can be huge in production!
        private void PrintReminders(string msg = null)
        {
            if (!Logger.IsVerbose3) return;

            var str = String.Format("{0}{1}{2}", (msg ?? "Current list of reminders:"), Environment.NewLine,
                Utils.EnumerableToString(localReminders, null, Environment.NewLine));
            Logger.Verbose3(str);
        }

        #endregion

        private class LocalReminderData
        {
            private readonly IRemindable remindable;
            private Stopwatch stopwatch;
            private readonly DateTime firstTickTime; // time for the first tick of this reminder
            private readonly TimeSpan period;
            private GrainReference GrainRef {  get { return Identity.GrainRef; } }
            private string ReminderName { get { return Identity.ReminderName; } }

            internal ReminderIdentity Identity { get; private set; }
            internal string ETag;
            internal IGrainTimer Timer;
            internal long LocalSequenceNumber; // locally, we use this for resolving races between the periodic table reader, and any concurrent local register/unregister requests

            internal LocalReminderData(ReminderEntry entry)
            {
                Identity = new ReminderIdentity(entry.GrainRef, entry.ReminderName);
                firstTickTime = entry.StartAt;
                period = entry.Period;
                remindable = entry.GrainRef.Cast<IRemindable>();
                ETag = entry.ETag;
                LocalSequenceNumber = -1;
            }

            public void StartTimer(Func<object, Task> asyncCallback, Logger Logger)
            {
                StopReminder(Logger); // just to make sure.
                var dueTimeSpan = CalculateDueTime();
                Timer = GrainTimer.FromTaskCallback(asyncCallback, this, dueTimeSpan, period, name: ReminderName);
                if (Logger.IsVerbose) Logger.Verbose("Reminder {0}, Due time{1}", this, dueTimeSpan);
                Timer.Start();
            }

            public void StopReminder(Logger Logger)
            {
                if (Timer != null)
                    Timer.Dispose();

                Timer = null;
            }

            private TimeSpan CalculateDueTime()
            {
                TimeSpan dueTimeSpan;
                var now = DateTime.UtcNow;
                if (now < firstTickTime) // if the time for first tick hasn't passed yet
                {
                    dueTimeSpan = firstTickTime.Subtract(now); // then duetime is duration between now and the first tick time
                }
                else // the first tick happened in the past ... compute duetime based on the first tick time, and period
                {
                    // formula used:
                    // due = period - 'time passed since last tick (==sinceLast)'
                    // due = period - ((Now - FirstTickTime) % period)
                    // explanation of formula:
                    // (Now - FirstTickTime) => gives amount of time since first tick happened
                    // (Now - FirstTickTime) % period => gives amount of time passed since the last tick should have triggered
                    var sinceFirstTick = now.Subtract(firstTickTime);
                    var sinceLastTick = TimeSpan.FromTicks(sinceFirstTick.Ticks % period.Ticks);
                    dueTimeSpan = period.Subtract(sinceLastTick);
                    // in corner cases, dueTime can be equal to period ... so, take another mod
                    dueTimeSpan = TimeSpan.FromTicks(dueTimeSpan.Ticks % period.Ticks);
                }
                return dueTimeSpan;
            }

            public async Task OnTimerTick(AverageTimeSpanStatistic tardinessStat, Logger Logger)
            {
                var before = DateTime.UtcNow;
                var status = TickStatus.NewStruct(firstTickTime, period, before);

                if (Logger.IsVerbose2) Logger.Verbose2("Triggering tick for {0}, status {1}, now {2}", this.ToString(), status, before);

                try
                {
                    if (null != stopwatch)
                    {
                        stopwatch.Stop();
                        var tardiness = stopwatch.Elapsed - period;
                        tardinessStat.AddSample(Math.Max(0, tardiness.Ticks));
                    }
                    await remindable.ReceiveReminder(ReminderName, status);

                    if (null == stopwatch)
                        stopwatch = new Stopwatch();

                    stopwatch.Restart();

                    var after = DateTime.UtcNow;
                    if (Logger.IsVerbose2)
                        Logger.Verbose2("Tick triggered for {0}, dt {1} sec, next@~ {2}", this.ToString(), (after - before).TotalSeconds,
                            // the next tick isn't actually scheduled until we return control to
                            // AsyncSafeTimer but we can approximate it by adding the period of the reminder
                            // to the after time.
                            after + period);
                }
                catch (Exception exc)
                {
                    var after = DateTime.UtcNow;
                    Logger.Error(
                        ErrorCode.RS_Tick_Delivery_Error,
                        String.Format("Could not deliver reminder tick for {0}, next {1}.",  this.ToString(), after + period),
                            exc);
                    // What to do with repeated failures to deliver a reminder's ticks?
                }
            }

            public override string ToString()
            {
                return string.Format("[{0}, {1}, {2}, {3}, {4}, {5}, {6}]",
                                        ReminderName,
                                        GrainRef.ToDetailedString(),
                                        period,
                                        LogFormatter.PrintDate(firstTickTime),
                                        ETag,
                                        LocalSequenceNumber,
                                        Timer == null ? "Not_ticking" : "Ticking");
            }
        }

        private struct ReminderIdentity : IEquatable<ReminderIdentity>
        {
            private readonly GrainReference grainRef;
            private readonly string reminderName;

            public GrainReference GrainRef { get { return grainRef; } }
            public string ReminderName { get { return reminderName; } }

            public ReminderIdentity(GrainReference grainRef, string reminderName)
            {
                if (grainRef == null)
                    throw new ArgumentNullException("grainRef");

                if (string.IsNullOrWhiteSpace(reminderName))
                    throw new ArgumentException("The reminder name is either null or whitespace.", "reminderName");

                this.grainRef = grainRef;
                this.reminderName = reminderName;
            }

            public bool Equals(ReminderIdentity other)
            {
                return grainRef.Equals(other.grainRef) && reminderName.Equals(other.reminderName);
            }

            public override bool Equals(object other)
            {
                return (other is ReminderIdentity) && Equals((ReminderIdentity)other);
            }

            public override int GetHashCode()
            {
                return unchecked((int)((uint)grainRef.GetHashCode() + (uint)reminderName.GetHashCode()));
            }
        }
    }
}
