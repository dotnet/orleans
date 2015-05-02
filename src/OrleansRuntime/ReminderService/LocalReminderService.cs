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
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Orleans.Runtime.Scheduler;
using Orleans.Runtime.ConsistentRing;


namespace Orleans.Runtime.ReminderService
{
    internal class LocalReminderService : SystemTarget, IReminderService, IRingRangeListener
    {
        private enum ReminderServiceStatus
        {
            Booting = 0,
            Started,
            Stopped,
        }

        private readonly Dictionary<ReminderIdentity, LocalReminderData> localReminders;
        private readonly IConsistentRingProvider ring;
        private readonly IReminderTable reminderTable;
        private readonly OrleansTaskScheduler scheduler;
        private ReminderServiceStatus status;
        private IRingRange myRange;
        private long localTableSequence;
        private GrainTimer listRefresher; // timer that refreshes our list of reminders to reflect global reminder table
        private readonly TaskCompletionSource<bool> startedTask;

        private readonly AverageTimeSpanStatistic tardinessStat;
        private readonly CounterStatistic ticksDeliveredStat;
        private readonly TraceLogger logger;

        internal LocalReminderService(SiloAddress addr, GrainId id, IConsistentRingProvider ring, OrleansTaskScheduler localScheduler, IReminderTable reminderTable)
            : base(id, addr)
        {
            logger = TraceLogger.GetLogger("ReminderService", TraceLogger.LoggerType.Runtime);

            localReminders = new Dictionary<ReminderIdentity, LocalReminderData>();
            this.ring = ring;
            scheduler = localScheduler;
            this.reminderTable = reminderTable;
            status = ReminderServiceStatus.Booting;
            myRange = null;
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
        public async Task Start()
        {
            myRange = ring.GetMyRange();
            logger.Info(ErrorCode.RS_ServiceStarting, "Starting reminder system target on: {0} x{1,8:X8}, with range {2}", Silo, Silo.GetConsistentHashCode(), myRange);

            // in case reminderTable is as grain, poke the grain to activate it, before slamming it with multipel parallel requests, which may create duplicate activations.
            await reminderTable.Init();
            await ReadAndUpdateReminders();
            logger.Info(ErrorCode.RS_ServiceStarted, "Reminder system target started OK on: {0} x{1,8:X8}, with range {2}", Silo, Silo.GetConsistentHashCode(), myRange);

            status = ReminderServiceStatus.Started;
            startedTask.TrySetResult(true);
            var random = new SafeRandom();
            var dueTime = random.NextTimeSpan(Constants.RefreshReminderList);
            listRefresher = GrainTimer.FromTaskCallback(
                    _ => ReadAndUpdateReminders(),
                    null,
                    dueTime,
                    Constants.RefreshReminderList,
                    name: "ReminderService.ReminderListRefresher");
            listRefresher.Start();
            ring.SubscribeToRangeChangeEvents(this);
        }

        public Task Stop()
        {
            logger.Info(ErrorCode.RS_ServiceStopping, "Stopping reminder system target");
            status = ReminderServiceStatus.Stopped;

            ring.UnSubscribeFromRangeChangeEvents(this);

            if (listRefresher != null)
            {
                listRefresher.Dispose();
                listRefresher = null;
            }
            foreach (LocalReminderData r in localReminders.Values)
                r.StopReminder(logger);
            
            // for a graceful shutdown, also handover reminder responsibilities to new owner, and update the ReminderTable
            // currently, this is taken care of by periodically reading the reminder table
            return TaskDone.Done;
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

            if(logger.IsVerbose) logger.Verbose(ErrorCode.RS_RegisterOrUpdate, "RegisterOrUpdateReminder: {0}", entry.ToString());
            await DoResponsibilitySanityCheck(grainRef, "RegisterReminder");
            var newEtag = await reminderTable.UpsertRow(entry);

            if (newEtag != null)
            {
                if (logger.IsVerbose) logger.Verbose("Registered reminder {0} in table, assigned localSequence {1}", entry, localTableSequence);
                entry.ETag = newEtag;
                StartAndAddTimer(entry);
                if (logger.IsVerbose3) PrintReminders();
                return new ReminderData(grainRef, reminderName, newEtag) as IGrainReminder;
            }

            var msg = string.Format("Could not register reminder {0} to reminder table due to a race. Please try again later.", entry);
            logger.Error(ErrorCode.RS_Register_TableError, msg);
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
            if(logger.IsVerbose) logger.Verbose(ErrorCode.RS_Unregister, "UnregisterReminder: {0}, LocalTableSequence: {1}", remData, localTableSequence);

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
                    if(logger.IsVerbose) logger.Verbose(ErrorCode.RS_Stop, "Stopped reminder {0}", reminder);
                    if (logger.IsVerbose3) PrintReminders(string.Format("After removing {0}.", reminder));
                }
                else
                {
                    // no-op
                    if(logger.IsVerbose) logger.Verbose(ErrorCode.RS_RemoveFromTable, "Removed reminder from table which I didn't have locally: {0}.", reminder);
                }
            }
            else
            {
                var msg = string.Format("Could not unregister reminder {0} from the reminder table, due to tag mismatch. You can retry.", reminder);
                logger.Error(ErrorCode.RS_Unregister_TableError, msg);
                throw new ReminderException(msg);
            }
        }

        public async Task<IGrainReminder> GetReminder(GrainReference grainRef, string reminderName)
        {
            if(logger.IsVerbose) logger.Verbose(ErrorCode.RS_GetReminder,"GetReminder: GrainReference={0} ReminderName={1}", grainRef.ToDetailedString(), reminderName);
            var entry = await reminderTable.ReadRow(grainRef, reminderName);
            return entry.ToIGrainReminder();
        }

        public async Task<List<IGrainReminder>> GetReminders(GrainReference grainRef)
        {
            if (logger.IsVerbose) logger.Verbose(ErrorCode.RS_GetReminders, "GetReminders: GrainReference={0}", grainRef.ToDetailedString());
            var tableData = await reminderTable.ReadRows(grainRef);
            return tableData.Reminders.Select(entry => entry.ToIGrainReminder()).ToList();
        }

        #endregion

        /// <summary>
        /// Attempt to retrieve reminders from the global reminder table
        /// </summary>
        private async Task ReadAndUpdateReminders()
        {
            // try to retrieve reminder from all my subranges
            myRange = ring.GetMyRange();
            if (logger.IsVerbose2) logger.Verbose2("My range= {0}", myRange);
            var acks = new List<Task>();
            foreach (SingleRange range in RangeFactory.GetSubRanges(myRange))
            {
                if (logger.IsVerbose2) logger.Verbose2("Reading rows for range {0}", range);
                acks.Add(ReadTableAndStartTimers(range));
            }
            await Task.WhenAll(acks);
            if (logger.IsVerbose3) PrintReminders();
        }


        #region Change in membership, e.g., failure of predecessor
        /// <summary>
        /// Actions to take when the range of this silo changes on the ring due to a failure or a join
        /// </summary>
        /// <param name="old">my previous responsibility range</param>
        /// <param name="now">my new/current responsibility range</param>
        /// <param name="increased">True: my responsibility increased, false otherwise</param>
        public void RangeChangeNotification(IRingRange old, IRingRange now, bool increased)
        {
            // run on my own turn & context
            scheduler.QueueTask(() => OnRangeChange(old, now, increased), this.SchedulingContext).Ignore();
        }

        private async Task OnRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            logger.Info(ErrorCode.RS_RangeChanged, "My range changed from {0} to {1} increased = {2}", oldRange, newRange, increased);
            myRange = newRange;
            if (status == ReminderServiceStatus.Started)
                await ReadAndUpdateReminders();
            else
                if (logger.IsVerbose) logger.Verbose("Ignoring range change until ReminderService is Started -- Current status = {0}", status);
        }
        #endregion

        #region Internal implementation methods

        private async Task ReadTableAndStartTimers(IRingRange range)
        {
            if (logger.IsVerbose) logger.Verbose("Reading rows from {0}", range.ToString());
            localTableSequence++;
            long cachedSequence = localTableSequence;

            try
            {
                var srange = (SingleRange)range;
                ReminderTableData table = await reminderTable.ReadRows(srange.Begin, srange.End); // get all reminders, even the ones we already have

                // if null is a valid value, it means that there's nothing to do.
                if (null == table && reminderTable is MockReminderTable) return; 

                var remindersNotInTable = new Dictionary<ReminderIdentity, LocalReminderData>(localReminders); // shallow copy
                if (logger.IsVerbose) logger.Verbose("For range {0}, I read in {1} reminders from table. LocalTableSequence {2}, CachedSequence {3}", range.ToString(), table.Reminders.Count, localTableSequence, cachedSequence);

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
                                if (logger.IsVerbose2) logger.Verbose2("In table, In local, Old, & Ticking {0}", localRem);
                                // it might happen that our local reminder is different than the one in the table, i.e., eTag is different
                                // if so, stop the local timer for the old reminder, and start again with new info
                                if (!localRem.ETag.Equals(entry.ETag))
                                // this reminder needs a restart
                                {
                                    if (logger.IsVerbose2) logger.Verbose2("{0} Needs a restart", localRem);
                                    localRem.StopReminder(logger);
                                    localReminders.Remove(localRem.Identity);
                                    if (ring.GetMyRange().InRange(entry.GrainRef))
                                    // if its not my responsibility, I shouldn't start it locally
                                    {
                                        StartAndAddTimer(entry);
                                    }
                                }
                            }
                            else // if not ticking
                            {
                                // no-op
                                if (logger.IsVerbose2) logger.Verbose2("In table, In local, Old, & Not Ticking {0}", localRem);
                            }
                        }
                        else // cachedSequence < localRem.LocalSequenceNumber ... // info read from table is older than local info
                        {
                            if (localRem.Timer != null) // if ticking
                            {
                                // no-op
                                if (logger.IsVerbose2) logger.Verbose2("In table, In local, Newer, & Ticking {0}", localRem);
                            }
                            else // if not ticking
                            {
                                // no-op
                                if (logger.IsVerbose2) logger.Verbose2("In table, In local, Newer, & Not Ticking {0}", localRem);
                            }
                        }
                    }
                    else // exists in table, but not locally
                    {
                        if (logger.IsVerbose2) logger.Verbose2("In table, Not in local, {0}", entry);
                        // create and start the reminder
                        if (ring.GetMyRange().InRange(entry.GrainRef)) // if its not my responsibility, I shouldn't start it locally
                            StartAndAddTimer(entry);
                    }
                    // keep a track of extra reminders ... this 'reminder' is useful, so remove it from extra list
                    remindersNotInTable.Remove(new ReminderIdentity(entry.GrainRef, entry.ReminderName));
                } // foreach reminder read from table

                // foreach reminder that is not in global table, but exists locally
                foreach (var reminder in remindersNotInTable.Values)
                {
                    if (cachedSequence < reminder.LocalSequenceNumber)
                    {
                        // no-op
                        if (logger.IsVerbose2) logger.Verbose2("Not in table, In local, Newer, {0}", reminder);
                    }
                    else // cachedSequence > reminder.LocalSequenceNumber
                    {
                        if (logger.IsVerbose2) logger.Verbose2("Not in table, In local, Old, so removing. {0}", reminder);
                        // remove locally
                        reminder.StopReminder(logger);
                        localReminders.Remove(reminder.Identity);
                    }
                }
            }
            catch (Exception exc)
            {
                logger.Error(ErrorCode.RS_FailedToReadTableAndStartTimer, "Failed to read rows from table.", exc);
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
                if (logger.IsVerbose) logger.Verbose(ErrorCode.RS_LocalStop, "Localy stopping reminder {0} as it is different than newly registered reminder {1}", prevReminder, entry);
                prevReminder.StopReminder(logger);
                localReminders.Remove(prevReminder.Identity);
            }

            var newReminder = new LocalReminderData(entry);
            localTableSequence++;
            newReminder.LocalSequenceNumber = localTableSequence;
            localReminders.Add(newReminder.Identity, newReminder);
            newReminder.StartTimer(AsyncTimerCallback, logger);
            if (logger.IsVerbose) logger.Verbose(ErrorCode.RS_Started, "Started reminder {0}.", entry.ToString());
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
            localRem.StopReminder(logger);
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
            await reminder.OnTimerTick(tardinessStat, logger);
        }

        #region Utility (less useful) methods

        private async Task DoResponsibilitySanityCheck(GrainReference grainRef, string debugInfo)
        {
            if (status != ReminderServiceStatus.Started)
                await startedTask.Task;
            
            if (!myRange.InRange(grainRef))
            {
                logger.Warn(ErrorCode.RS_NotResponsible, "I shouldn't have received request '{0}' for {1}. It is not in my responsibility range: {2}",
                    debugInfo, grainRef.ToDetailedString(), myRange);
                // For now, we still let the caller proceed without throwing an exception... the periodical mechanism will take care of reminders being registered at the wrong silo
                // otherwise, we can either reject the request, or re-route the request
            }
        }

        // Note: The list of reminders can be huge in production!
        private void PrintReminders(string msg = null)
        {
            if (!logger.IsVerbose3) return;

            var str = String.Format("{0}{1}{2}", (msg ?? "Current list of reminders:"), Environment.NewLine,
                Utils.EnumerableToString(localReminders, null, Environment.NewLine));
            logger.Verbose3(str);
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
            internal GrainTimer Timer;
            internal long LocalSequenceNumber; // locally, we use this for resolving races between the periodic table reader, and any concurrent local register/unregister requests
            
            internal LocalReminderData(ReminderEntry entry)
            {
                Identity = new ReminderIdentity(entry.GrainRef, entry.ReminderName);
                firstTickTime = entry.StartAt;
                period = entry.Period;
                remindable = RemindableFactory.Cast(entry.GrainRef);
                ETag = entry.ETag;
                LocalSequenceNumber = -1;
            }

            public void StartTimer(Func<object, Task> asyncCallback, TraceLogger logger)
            {
                StopReminder(logger); // just to make sure.
                var dueTimeSpan = CalculateDueTime();
                Timer = GrainTimer.FromTaskCallback(asyncCallback, this, dueTimeSpan, period, name: ReminderName);
                if (logger.IsVerbose) logger.Verbose("Reminder {0}, Due time{1}", this, dueTimeSpan);
                Timer.Start();
            }

            public void StopReminder(TraceLogger logger)
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

            public async Task OnTimerTick(AverageTimeSpanStatistic tardinessStat, TraceLogger logger)
            {
                var before = DateTime.UtcNow;
                var status = TickStatus.NewStruct(firstTickTime, period, before);

                if (logger.IsVerbose2) logger.Verbose2("Triggering tick for {0}, status {1}, now {2}", this.ToString(), status, before);

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
                    if (logger.IsVerbose2) 
                        logger.Verbose2("Tick triggered for {0}, dt {1} sec, next@~ {2}", this.ToString(), (after - before).TotalSeconds, 
                            // the next tick isn't actually scheduled until we return control to
                            // AsyncSafeTimer but we can approximate it by adding the period of the reminder
                            // to the after time.
                            after + period);
                }
                catch (Exception exc)
                {
                    var after = DateTime.UtcNow;
                    logger.Error(
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
                                        TraceLogger.PrintDate(firstTickTime),
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
