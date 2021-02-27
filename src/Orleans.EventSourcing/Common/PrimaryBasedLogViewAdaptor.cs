using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.EventSourcing.Common
{
    /// <summary>
    /// A general template for constructing log view adaptors that are based on
    /// a sequentially read and written primary. We use this to construct 
    /// a variety of different log-consistency providers, all following the same basic pattern 
    /// (read and write latest view from/to primary, and send notifications after writing).
    ///<para>
    /// Note that the log itself is transient, i.e. not actually saved to storage - only the latest view and some 
    /// metadata (the log position, and write flags) is stored in the primary. 
    /// It is safe to interleave calls to this adaptor (using grain scheduler only, of course).
    /// </para>
    ///<para>
    /// Subclasses override ReadAsync and WriteAsync to read from / write to primary.
    /// Calls to the primary are serialized, i.e. never interleave.
    /// </para>
    /// </summary>
    /// <typeparam name="TLogView">The user-defined view of the log</typeparam>
    /// <typeparam name="TLogEntry">The type of the log entries</typeparam>
    /// <typeparam name="TSubmissionEntry">The type of submission entries stored in pending queue</typeparam>
    public abstract class PrimaryBasedLogViewAdaptor<TLogView, TLogEntry, TSubmissionEntry> : ILogViewAdaptor<TLogView, TLogEntry>
    where TLogView : class, new()
        where TLogEntry : class
        where TSubmissionEntry : SubmissionEntry<TLogEntry>
    {
        /// <summary>
        /// Set confirmed view the initial value (a view of the empty log)
        /// </summary>
        protected abstract void InitializeConfirmedView(TLogView initialstate);

        /// <summary>
        /// Read cached global state.
        /// </summary>
        protected abstract TLogView LastConfirmedView();

        /// <summary>
        /// Read version of cached global state.
        /// </summary>
        protected abstract int GetConfirmedVersion();

        /// <summary>
        /// Read the latest primary state. Must block/retry until successful.
        /// Should not throw exceptions, but record them in <see cref="LastPrimaryIssue"/>
        /// </summary>
        /// <returns></returns>
        protected abstract Task ReadAsync();

        /// <summary>
        /// Apply pending entries to the primary. Must block/retry until successful. 
        /// Should not throw exceptions, but record them in <see cref="LastPrimaryIssue"/>
        /// </summary>
        protected abstract Task<int> WriteAsync();

        /// <summary>
        /// Create a submission entry for the submitted log entry. 
        /// Using a type parameter so we can add protocol-specific info to this class.
        /// </summary>
        /// <returns></returns>
        protected abstract TSubmissionEntry MakeSubmissionEntry(TLogEntry entry);

        /// <summary>
        /// Whether this cluster supports submitting updates
        /// </summary>
        protected virtual bool SupportSubmissions {  get { return true;  } }

        /// <summary>
        /// Handle protocol messages.
        /// </summary>
        protected virtual Task<ILogConsistencyProtocolMessage> OnMessageReceived(ILogConsistencyProtocolMessage payload)
        {
            // subclasses that define custom protocol messages must override this
            throw new NotImplementedException();
        }

        public virtual Task<IReadOnlyList<TLogEntry>> RetrieveLogSegment(int fromVersion, int length)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Handle notification messages. Override this to handle notification subtypes.
        /// </summary>
        protected virtual void OnNotificationReceived(INotificationMessage payload)
        {        
            var msg = payload as VersionNotificationMessage; 
            if (msg != null)
            {
                if (msg.Version > lastVersionNotified)
                    lastVersionNotified = msg.Version;
                return;
            }

            var batchmsg = payload as BatchedNotificationMessage;
            if (batchmsg != null)
            {
                foreach (var bm in batchmsg.Notifications)
                    OnNotificationReceived(bm);
                return;
            }

            // subclass should have handled this in override
            throw new ProtocolTransportException(string.Format("message type {0} not handled by OnNotificationReceived", payload.GetType().FullName));
        }

        /// <summary>
        /// The last version we have been notified of
        /// </summary>
        private int lastVersionNotified;

        /// <summary>
        /// Process stored notifications during worker cycle. Override to handle notification subtypes.
        /// </summary>
        protected virtual void ProcessNotifications()
        {
            if (lastVersionNotified > this.GetConfirmedVersion())
            {
                Services.Log(LogLevel.Debug, "force refresh because of version notification v{0}", lastVersionNotified);
                needRefresh = true;
            }
        }

        /// <summary>
        /// Merge two notification messages, for batching. Override to handle notification subtypes.
        /// </summary>
        protected virtual INotificationMessage Merge(INotificationMessage earliermessage, INotificationMessage latermessage)
        {
            return new VersionNotificationMessage()
            {
                Version = latermessage.Version
            };
        }

        /// <summary>
        /// The grain that is using this adaptor.
        /// </summary>
        protected ILogViewAdaptorHost<TLogView, TLogEntry> Host { get; private set; }

        /// <summary>
        /// The runtime services required for implementing notifications between grain instances in different cluster.
        /// </summary>
        protected ILogConsistencyProtocolServices Services { get; private set; }

        /// <summary>
        /// Construct an instance, for the given parameters.
        /// </summary>
        protected PrimaryBasedLogViewAdaptor(ILogViewAdaptorHost<TLogView, TLogEntry> host, 
            TLogView initialstate, ILogConsistencyProtocolServices services)
        {
            Debug.Assert(host != null && services != null && initialstate != null);
            this.Host = host;
            this.Services = services;
            InitializeConfirmedView(initialstate);
            worker = new BatchWorkerFromDelegate(Work);
        }

        /// <inheritdoc/>
        public virtual Task PreOnActivate()
        {
            Services.Log(LogLevel.Trace, "PreActivation Started");

            // this flag indicates we have not done an initial load from storage yet
            // we do not act on this yet, but wait until after user OnActivate has run. 
            needInitialRead = true;

            Services.Log(LogLevel.Trace, "PreActivation Complete");

            return Task.CompletedTask;
        }

        public virtual Task PostOnActivate()
        {
            Services.Log(LogLevel.Trace, "PostActivation Started");

            // start worker, if it has not already happened
            if (needInitialRead)
                worker.Notify();

            Services.Log(LogLevel.Trace, "PostActivation Complete");

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public virtual async Task PostOnDeactivate()
        {
            Services.Log(LogLevel.Trace, "Deactivation Started");

            while (!worker.IsIdle())
            {
                await worker.WaitForCurrentWorkToBeServiced();
            }

            Services.Log(LogLevel.Trace, "Deactivation Complete");
        }


        // the currently submitted, unconfirmed entries. 
        private readonly List<TSubmissionEntry> pending = new List<TSubmissionEntry>();


        /// called at beginning of WriteAsync to the current tentative state
        protected TLogView CopyTentativeState()
        {
            var state = TentativeView;
            tentativeStateInternal = null; // to avoid aliasing
            return state;
        }
        /// called at beginning of WriteAsync to the current batch of updates
        protected TSubmissionEntry[] GetCurrentBatchOfUpdates()
        {
            return pending.ToArray(); // must use a copy
        }
        /// called at beginning of WriteAsync to get current number of pending updates
        protected int GetNumberPendingUpdates()
        {
            return pending.Count;
        }

        /// <summary>
        ///  Tentative State. Represents Stable State + effects of pending updates.
        ///  Computed lazily (null if not in use)
        /// </summary>
        private TLogView tentativeStateInternal;

        /// <summary>
        /// A flag that indicates to the worker that the client wants to refresh the state
        /// </summary>
        private bool needRefresh;

        /// <summary>
        /// A flag that indicates that we have not read global state at all yet, and should do so
        /// </summary>
        private bool needInitialRead;

        /// <summary>
        /// Background worker which asynchronously sends operations to the leader
        /// </summary>
        private BatchWorker worker;




        /// statistics gathering. Is null unless stats collection is turned on.
        protected LogConsistencyStatistics stats = null;


        /// For use by protocols. Determines if this cluster is part of the configured multicluster.
        protected bool IsMyClusterJoined()
        {
            return true;
        }

        /// <summary>
        /// Block until this cluster is joined to the multicluster.
        /// </summary>
        protected async Task EnsureClusterJoinedAsync()
        {
            while (!IsMyClusterJoined())
            {
                Services.Log(LogLevel.Debug, "Waiting for join");
                await Task.Delay(5000);
            }
        }

        /// <inheritdoc />
        public void Submit(TLogEntry logEntry)
        {
            if (!SupportSubmissions)
                throw new InvalidOperationException("provider does not support submissions on cluster " + Services.MyClusterId);

            if (stats != null) stats.EventCounters["SubmitCalled"]++;

            Services.Log(LogLevel.Trace, "Submit");

            SubmitInternal(DateTime.UtcNow, logEntry);

            worker.Notify();
        }

        /// <inheritdoc />
        public void SubmitRange(IEnumerable<TLogEntry> logEntries)
        {
            if (!SupportSubmissions)
                throw new InvalidOperationException("Provider does not support submissions on cluster " + Services.MyClusterId);

            if (stats != null) stats.EventCounters["SubmitRangeCalled"]++;

            Services.Log(LogLevel.Trace, "SubmitRange");

            var time = DateTime.UtcNow;

            foreach (var e in logEntries)
                SubmitInternal(time, e);

            worker.Notify();
        }

        /// <inheritdoc />
        public Task<bool> TryAppend(TLogEntry logEntry)
        {
            if (!SupportSubmissions)
                throw new InvalidOperationException("Provider does not support submissions on cluster " + Services.MyClusterId);

            if (stats != null) stats.EventCounters["TryAppendCalled"]++;

            Services.Log(LogLevel.Trace, "TryAppend");

            var promise = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            SubmitInternal(DateTime.UtcNow, logEntry, GetConfirmedVersion() + pending.Count, promise);

            worker.Notify();

            return promise.Task;
        }

        /// <inheritdoc />
        public Task<bool> TryAppendRange(IEnumerable<TLogEntry> logEntries)
        {
            if (!SupportSubmissions)
                throw new InvalidOperationException("Provider does not support submissions on cluster " + Services.MyClusterId);

            if (stats != null) stats.EventCounters["TryAppendRangeCalled"]++;

            Services.Log(LogLevel.Trace, "TryAppendRange");

            var promise = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var time = DateTime.UtcNow;
            var pos = GetConfirmedVersion() + pending.Count;

            bool first = true;
            foreach (var e in logEntries)
            {
                SubmitInternal(time, e, pos++, first ? promise : null);
                first = false;
            }

            worker.Notify();

            return promise.Task;
        }


        private const int unconditional = -1;

        private void SubmitInternal(DateTime time, TLogEntry logentry, int conditionalPosition = unconditional, TaskCompletionSource<bool> resultPromise = null)
        {
            // create a submission entry
            var submissionentry = this.MakeSubmissionEntry(logentry);
            submissionentry.SubmissionTime = time;
            submissionentry.ResultPromise = resultPromise;
            submissionentry.ConditionalPosition = conditionalPosition;

            // add submission to queue
            pending.Add(submissionentry);

            // if we have a tentative state in use, update it
            if (this.tentativeStateInternal != null)
            {
                try
                {
                    Host.UpdateView(this.tentativeStateInternal, logentry);
                }
                catch (Exception e)
                {
                    Services.CaughtUserCodeException("UpdateView", nameof(SubmitInternal), e);
                }
            }

            try
            {
                Host.OnViewChanged(true, false);
            }
            catch (Exception e)
            {
                Services.CaughtUserCodeException("OnViewChanged", nameof(SubmitInternal), e);
            }
        }

        /// <inheritdoc />
        public TLogView TentativeView
        {
            get
            {
                if (stats != null)
                    stats.EventCounters["TentativeViewCalled"]++;

                if (tentativeStateInternal == null)
                    CalculateTentativeState();

                return tentativeStateInternal;
            }
        }

        /// <inheritdoc />
        public TLogView ConfirmedView
        {
            get
            {
                if (stats != null)
                    stats.EventCounters["ConfirmedViewCalled"]++;

                return LastConfirmedView();
            }
        }

        /// <inheritdoc />
        public int ConfirmedVersion
        {
            get
            {
                if (stats != null)
                    stats.EventCounters["ConfirmedVersionCalled"]++;

                return GetConfirmedVersion();
            }
        }

        /// <summary>
        /// Called from network
        /// </summary>
        /// <param name="payLoad"></param>
        /// <returns></returns>
        public async Task<ILogConsistencyProtocolMessage> OnProtocolMessageReceived(ILogConsistencyProtocolMessage payLoad)
        {
            var notificationMessage = payLoad as INotificationMessage;

            if (notificationMessage != null)
            {
                Services.Log(LogLevel.Debug, "NotificationReceived v{0}", notificationMessage.Version);

                OnNotificationReceived(notificationMessage);

                // poke worker so it will process the notifications
                worker.Notify();

                return null;
            }
            else
            {
                //it's a protocol message
                return await OnMessageReceived(payLoad);
            }
        }

        /// <summary>
        /// method is virtual so subclasses can add their own events
        /// </summary>
        public virtual void EnableStatsCollection()
        {

            stats = new LogConsistencyStatistics()
            {
                EventCounters = new Dictionary<string, long>(),
                StabilizationLatenciesInMsecs = new List<int>()
            };

            stats.EventCounters.Add("TentativeViewCalled", 0);
            stats.EventCounters.Add("ConfirmedViewCalled", 0);
            stats.EventCounters.Add("ConfirmedVersionCalled", 0);
            stats.EventCounters.Add("SubmitCalled", 0);
            stats.EventCounters.Add("SubmitRangeCalled", 0);
            stats.EventCounters.Add("TryAppendCalled", 0);
            stats.EventCounters.Add("TryAppendRangeCalled", 0);
            stats.EventCounters.Add("ConfirmSubmittedEntriesCalled", 0);
            stats.EventCounters.Add("SynchronizeNowCalled", 0);

            stats.EventCounters.Add("WritebackEvents", 0);

            stats.StabilizationLatenciesInMsecs = new List<int>();
        }

        /// <summary>
        /// Disable stats collection
        /// </summary>
        public void DisableStatsCollection()
        {
            stats = null;
        }

        /// <summary>
        /// Get states
        /// </summary>
        /// <returns></returns>
        public LogConsistencyStatistics GetStats()
        {
            return stats;
        }

        private void CalculateTentativeState()
        {
            // copy the confirmed view
            this.tentativeStateInternal = Services.DeepCopy(LastConfirmedView());

            // Now apply all operations in pending 
            foreach (var u in this.pending)
                try
                {
                    Host.UpdateView(this.tentativeStateInternal, u.Entry);
                }
                catch (Exception e)
                {
                    Services.CaughtUserCodeException("UpdateView", nameof(CalculateTentativeState), e);
                }
        }


        /// <summary>
        /// batch worker performs reads from and writes to global state.
        /// only one work cycle is active at any time.
        /// </summary>
        internal async Task Work()
        {
            Services.Log(LogLevel.Debug, "<1 ProcessNotifications");

            var version = GetConfirmedVersion();

            ProcessNotifications();

            Services.Log(LogLevel.Debug, "<2 NotifyViewChanges");

            NotifyViewChanges(ref version);

            bool haveToWrite = (pending.Count != 0);

            bool haveToRead = needInitialRead || (needRefresh && !haveToWrite);

            Services.Log(LogLevel.Debug, "<3 Storage htr={0} htw={1}", haveToRead, haveToWrite);

            try
            {
                if (haveToRead)
                {
                    needRefresh = needInitialRead = false; // retrieving fresh version

                    await ReadAsync();

                    NotifyViewChanges(ref version);
                }

                if (haveToWrite)
                {
                    needRefresh = needInitialRead = false; // retrieving fresh version

                    await UpdatePrimary();

                    if (stats != null) stats.EventCounters["WritebackEvents"]++;
                }

            }
            catch (Exception e)
            {
                // this should never happen - we are supposed to catch and store exceptions 
                // in the correct place (LastPrimaryException or notification trackers)
                Services.ProtocolError($"Exception in Worker Cycle: {e}", true);

            }

            Services.Log(LogLevel.Debug, "<4 Done");
        }


        /// <summary>
        /// This function stores the operations in the pending queue as a batch to the primary.
        /// Retries until some batch commits or there are no updates left.
        /// </summary>
        internal async Task UpdatePrimary()
        {
            int version = GetConfirmedVersion();

            while (true)
            {
                try
                {
                    // find stale conditional updates, remove them, and notify waiters
                    RemoveStaleConditionalUpdates();

                    if (pending.Count == 0)
                        return; // no updates to write.

                    // try to write the updates as a batch
                    var writeResult = await WriteAsync();

                    NotifyViewChanges(ref version, writeResult);

                    // if the batch write failed due to conflicts, retry.
                    if (writeResult == 0)
                        continue;

                    try
                    {
                        Host.OnViewChanged(false, true);
                    }
                    catch (Exception e)
                    {
                        Services.CaughtUserCodeException("OnViewChanged", nameof(UpdatePrimary), e);
                    }

                    // notify waiting promises of the success of conditional updates
                    NotifyPromises(writeResult, true);

                    // record stabilization time, for statistics
                    if (stats != null)
                    {
                        var timeNow = DateTime.UtcNow;
                        for (int i = 0; i < writeResult; i++)
                        {
                            var latency = timeNow - pending[i].SubmissionTime;
                            stats.StabilizationLatenciesInMsecs.Add(latency.Milliseconds);
                        }
                    }

                    // remove completed updates from queue
                    pending.RemoveRange(0, writeResult);

                    return;
                }
                catch (Exception e)
                {
                    // this should never happen - we are supposed to catch and store exceptions 
                    // in the correct place (LastPrimaryException or notification trackers)
                    Services.ProtocolError($"Exception in {nameof(UpdatePrimary)}: {e}", true);
                }
            }
        }
        

        private void NotifyViewChanges(ref int version, int numWritten = 0)
        {
            var v = GetConfirmedVersion();
            bool tentativeChanged = (v != version + numWritten);
            bool confirmedChanged = (v != version);
            if (tentativeChanged || confirmedChanged)
            {
                tentativeStateInternal = null; // conservative.
                try
                {
                    Host.OnViewChanged(tentativeChanged, confirmedChanged);
                }
                catch (Exception e)
                {
                    Services.CaughtUserCodeException("OnViewChanged", nameof(NotifyViewChanges), e);
                }
                version = v;
            }
        }

        /// <summary>
        /// Store the last issue that occurred while reading or updating primary.
        /// Is null if successful.
        /// </summary>
        protected RecordedConnectionIssue LastPrimaryIssue;

     

        /// <inheritdoc />
        public async Task Synchronize()
        {
            if (stats != null)
                stats.EventCounters["SynchronizeNowCalled"]++;

            Services.Log(LogLevel.Debug, "SynchronizeNowStart");

            needRefresh = true;
            await worker.NotifyAndWaitForWorkToBeServiced();

            Services.Log(LogLevel.Debug, "SynchronizeNowComplete");
        }

        /// <inheritdoc/>
        public IEnumerable<TLogEntry> UnconfirmedSuffix
        {
            get
            {
                return pending.Select(te => te.Entry);
            }
        }

        /// <inheritdoc />
        public async Task ConfirmSubmittedEntries()
        {
            if (stats != null)
                stats.EventCounters["ConfirmSubmittedEntriesCalled"]++;

            Services.Log(LogLevel.Debug, "ConfirmSubmittedEntriesStart");

            if (pending.Count != 0)
                await worker.WaitForCurrentWorkToBeServiced();

            Services.Log(LogLevel.Debug, "ConfirmSubmittedEntriesEnd");
        }

        /// <summary>
        /// send failure notifications
        /// </summary>
        protected void NotifyPromises(int count, bool success)
        {
            for (int i = 0; i < count; i++)
            {
                var promise = pending[i].ResultPromise;
                if (promise != null)
                    promise.SetResult(success);
            }
        }

        /// <summary>
        /// go through updates and remove all the conditional updates that have already failed
        /// </summary>
        protected void RemoveStaleConditionalUpdates()
        {
            int version = GetConfirmedVersion();
            bool foundFailedConditionalUpdates = false;

            for (int pos = 0; pos < pending.Count; pos++)
            {
                var submissionEntry = pending[pos];
                if (submissionEntry.ConditionalPosition != unconditional
                    && (foundFailedConditionalUpdates ||
                           submissionEntry.ConditionalPosition != (version + pos)))
                {
                    foundFailedConditionalUpdates = true;
                    if (submissionEntry.ResultPromise != null)
                        submissionEntry.ResultPromise.SetResult(false);
                }
                pos++;
            }

            if (foundFailedConditionalUpdates)
            {
                pending.RemoveAll(e => e.ConditionalPosition != unconditional);
                tentativeStateInternal = null;
                try
                {
                    Host.OnViewChanged(true, false);
                }
                catch (Exception e)
                {
                    Services.CaughtUserCodeException("OnViewChanged", nameof(RemoveStaleConditionalUpdates), e);
                }
            }
        }
    }

    /// <summary>
    /// Base class for submission entries stored in pending queue. 
    /// </summary>
    /// <typeparam name="TLogEntry">The type of entry for this submission</typeparam>
    public class SubmissionEntry<TLogEntry>
    {
        /// <summary> The log entry that is submitted. </summary>
        public TLogEntry Entry;

        /// <summary> A timestamp for this submission. </summary>
        public DateTime SubmissionTime;

        /// <summary> For conditional updates, a promise that resolves once it is known whether the update was successful or not.</summary>
        public TaskCompletionSource<bool> ResultPromise;

        /// <summary> For conditional updates, the log position at which this update is supposed to be applied. </summary>
        public int ConditionalPosition;
    }


}
