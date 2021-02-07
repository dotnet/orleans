using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Storage;
using Orleans.EventSourcing.Common;

namespace Orleans.EventSourcing.LogStorage
{
    /// <summary>
    /// A log view adaptor that wraps around a traditional storage adaptor, and uses batching and e-tags
    /// to append entries.
    ///<para>
    /// The log itself is transient, i.e. not actually saved to storage - only the latest view and some 
    /// metadata (the log position, and write flags) are stored. 
    /// </para>
    /// </summary>
    /// <typeparam name="TLogView">Type of log view</typeparam>
    /// <typeparam name="TLogEntry">Type of log entry</typeparam>
    internal class LogViewAdaptor<TLogView, TLogEntry> : PrimaryBasedLogViewAdaptor<TLogView, TLogEntry, SubmissionEntry<TLogEntry>> where TLogView : class, new() where TLogEntry : class
    {
        /// <summary>
        /// Initialize a StorageProviderLogViewAdaptor class
        /// </summary>
        public LogViewAdaptor(ILogViewAdaptorHost<TLogView, TLogEntry> host, TLogView initialState, IGrainStorage globalGrainStorage, string grainTypeName, ILogConsistencyProtocolServices services)
            : base(host, initialState, services)
        {
            this.globalGrainStorage = globalGrainStorage;
            this.grainTypeName = grainTypeName;
        }


        private const int maxEntriesInNotifications = 200;


        IGrainStorage globalGrainStorage;
        string grainTypeName;   

        // the object containing the entire log, as retrieved from / sent to storage
        LogStateWithMetaDataAndETag<TLogEntry> GlobalLog;

        // the confirmed view
        TLogView ConfirmedViewInternal;
        int ConfirmedVersionInternal;

        /// <inheritdoc/>
        protected override TLogView LastConfirmedView()
        {
            return ConfirmedViewInternal;
        }

        /// <inheritdoc/>
        protected override int GetConfirmedVersion()
        {
            return ConfirmedVersionInternal;
        }

        /// <inheritdoc/>
        protected override void InitializeConfirmedView(TLogView initialstate)
        {
            GlobalLog = new LogStateWithMetaDataAndETag<TLogEntry>();
            ConfirmedViewInternal = initialstate;
            ConfirmedVersionInternal = 0;
        }

        private void UpdateConfirmedView()
        {
            for (int i = ConfirmedVersionInternal; i < GlobalLog.StateAndMetaData.Log.Count; i++)
            {
                try
                {
                    Host.UpdateView(ConfirmedViewInternal, GlobalLog.StateAndMetaData.Log[i]);
                }
                catch (Exception e)
                {
                    Services.CaughtUserCodeException("UpdateView", nameof(UpdateConfirmedView), e);
                }
            }
            ConfirmedVersionInternal = GlobalLog.StateAndMetaData.GlobalVersion;
        }


        /// <inheritdoc/>
        public override Task<IReadOnlyList<TLogEntry>> RetrieveLogSegment(int fromVersion, int toVersion)
        {

            // make a copy of the entries in the range asked for
            IReadOnlyList<TLogEntry> segment = GlobalLog.StateAndMetaData.Log.GetRange(fromVersion, (toVersion - fromVersion));

            return Task.FromResult(segment);
        }

        // no special tagging is required, thus we create a plain submission entry
        /// <inheritdoc/>
        protected override SubmissionEntry<TLogEntry> MakeSubmissionEntry(TLogEntry entry)
        {
            return new SubmissionEntry<TLogEntry>() { Entry = entry };
        }


        /// <inheritdoc/>
        protected override async Task ReadAsync()
        {
            enter_operation("ReadAsync");

            while (true)
            {
                try
                {
                    // for manual testing
                    //await Task.Delay(5000);

                    await globalGrainStorage.ReadStateAsync(grainTypeName, Services.GrainReference, GlobalLog);

                    Services.Log(LogLevel.Debug, "read success {0}", GlobalLog);

                    UpdateConfirmedView();

                    LastPrimaryIssue.Resolve(Host, Services);

                    break; // successful
                }
                catch (Exception e)
                {
                    LastPrimaryIssue.Record(new ReadFromLogStorageFailed() { Exception = e }, Host, Services);
                }

                Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);

                await LastPrimaryIssue.DelayBeforeRetry();
            }

            exit_operation("ReadAsync");
        }


        /// <inheritdoc/>
        protected override async Task<int> WriteAsync()
        {
            enter_operation("WriteAsync");

            var updates = GetCurrentBatchOfUpdates();
            bool batchsuccessfullywritten = false;

            var writebit = GlobalLog.StateAndMetaData.FlipBit(Services.MyClusterId);
            foreach (var x in updates)
                GlobalLog.StateAndMetaData.Log.Add(x.Entry);

            try
            {
                // for manual testing
                //await Task.Delay(5000);

                await globalGrainStorage.WriteStateAsync(grainTypeName, Services.GrainReference, GlobalLog);

                batchsuccessfullywritten = true;

                Services.Log(LogLevel.Debug, "write ({0} updates) success {1}", updates.Length, GlobalLog);

                UpdateConfirmedView();

                LastPrimaryIssue.Resolve(Host, Services);
            }
            catch (Exception e)
            {
                LastPrimaryIssue.Record(new UpdateLogStorageFailed() { Exception = e }, Host, Services);
            }

            if (!batchsuccessfullywritten)
            {
                Services.Log(LogLevel.Debug, "write apparently failed {0}", LastPrimaryIssue);

                while (true) // be stubborn until we can read what is there
                {

                    await LastPrimaryIssue.DelayBeforeRetry();

                    try
                    {
                        await globalGrainStorage.ReadStateAsync(grainTypeName, Services.GrainReference, GlobalLog);

                        Services.Log(LogLevel.Debug, "read success {0}", GlobalLog);

                        UpdateConfirmedView();

                        LastPrimaryIssue.Resolve(Host, Services);

                        break;
                    }
                    catch (Exception e)
                    {
                        LastPrimaryIssue.Record(new ReadFromLogStorageFailed() { Exception = e }, Host, Services);
                    }

                    Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);
                }

                // check if last apparently failed write was in fact successful

                if (writebit == GlobalLog.StateAndMetaData.GetBit(Services.MyClusterId))
                {
                    Services.Log(LogLevel.Debug, "last write ({0} updates) was actually a success {1}", updates.Length, GlobalLog);

                    batchsuccessfullywritten = true;
                }
            }

            exit_operation("WriteAsync");

            if (!batchsuccessfullywritten)
                return 0;

            return updates.Length;
        }


        /// <summary>
        /// Describes a connection issue that occurred when updating the primary storage.
        /// </summary>
        [Serializable]
        public class UpdateLogStorageFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"write entire log to storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }


        /// <summary>
        /// Describes a connection issue that occurred when reading from the primary storage.
        /// </summary>
        [Serializable]
        public class ReadFromLogStorageFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"read entire log from storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }


        /// <summary>
        /// A notification message sent to remote instances after updating this grain in storage.
        /// </summary>
        [Serializable]
        protected class UpdateNotificationMessage : INotificationMessage 
        {
            /// <inheritdoc/>
            public int Version { get; set; }

            /// <summary> The cluster that performed the update </summary>
            public string Origin { get; set; }

            /// <summary> The list of updates that were applied </summary>
            public List<TLogEntry> Updates { get; set; }

            /// <summary> The e-tag of the storage after applying the updates</summary>
            public string ETag { get; set; }

            /// <inheritdoc/>
            public override string ToString()
            {
                return string.Format("v{0} ({1} updates by {2}) etag={3}", Version, Updates.Count, Origin, ETag);
            }
         }

        /// <inheritdoc/>
        protected override INotificationMessage Merge(INotificationMessage earlierMessage, INotificationMessage laterMessage)
        {
            var earlier = earlierMessage as UpdateNotificationMessage;
            var later = laterMessage as UpdateNotificationMessage;

            if (earlier != null
                && later != null
                && earlier.Origin == later.Origin
                && earlier.Version + later.Updates.Count == later.Version
                && earlier.Updates.Count + later.Updates.Count < maxEntriesInNotifications)

                return new UpdateNotificationMessage()
                {
                    Version = later.Version,
                    Origin = later.Origin,
                    Updates = earlier.Updates.Concat(later.Updates).ToList(),
                    ETag = later.ETag
                };

            else
                return base.Merge(earlierMessage, laterMessage); // keep only the version number
        }

        private SortedList<long, UpdateNotificationMessage> notifications = new SortedList<long,UpdateNotificationMessage>();

        /// <inheritdoc/>
        protected override void OnNotificationReceived(INotificationMessage payload)
        {
            var um = payload as UpdateNotificationMessage;
            if (um != null)
                notifications.Add(um.Version - um.Updates.Count, um);
            else
                base.OnNotificationReceived(payload);
        }

        /// <inheritdoc/>
        protected override void ProcessNotifications()
        {
            // discard notifications that are behind our already confirmed state
            while (notifications.Count > 0 && notifications.ElementAt(0).Key < GlobalLog.StateAndMetaData.GlobalVersion)
            {
                Services.Log(LogLevel.Debug, "discarding notification {0}", notifications.ElementAt(0).Value);
                notifications.RemoveAt(0);
            }

            // process notifications that reflect next global version
            while (notifications.Count > 0 && notifications.ElementAt(0).Key == GlobalLog.StateAndMetaData.GlobalVersion)
            {
                var updateNotification = notifications.ElementAt(0).Value;
                notifications.RemoveAt(0);

                // append all operations in pending 
                foreach (var u in updateNotification.Updates)
                    GlobalLog.StateAndMetaData.Log.Add(u);
                  
                GlobalLog.StateAndMetaData.FlipBit(updateNotification.Origin);

                GlobalLog.ETag = updateNotification.ETag;

                UpdateConfirmedView();

                Services.Log(LogLevel.Debug, "notification success ({0} updates) {1}", updateNotification.Updates.Count, GlobalLog);
            }

            Services.Log(LogLevel.Trace, "unprocessed notifications in queue: {0}", notifications.Count);

            base.ProcessNotifications();
         
        }


#if DEBUG
        bool operation_in_progress;
#endif

        [Conditional("DEBUG")]
        private void enter_operation(string name)
        {
#if DEBUG
            Services.Log(LogLevel.Trace, "/-- enter {0}", name);
            Debug.Assert(!operation_in_progress);
            operation_in_progress = true;
#endif
        }

        [Conditional("DEBUG")]
        private void exit_operation(string name)
        {
#if DEBUG
            Services.Log(LogLevel.Trace, "\\-- exit {0}", name);
            Debug.Assert(operation_in_progress);
            operation_in_progress = false;
#endif
        }
    }
}
