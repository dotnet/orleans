using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Storage;
using Orleans.EventSourcing.Common;

namespace Orleans.EventSourcing.StateStorage
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
        private readonly IGrainStorage globalGrainStorage;
        private readonly string grainTypeName;        // stores the confirmed state including metadata
        private GrainStateWithMetaDataAndETag<TLogView> GlobalStateCache;

        /// <inheritdoc/>
        protected override TLogView LastConfirmedView()
        {
            return GlobalStateCache.StateAndMetaData.State;
        }

        /// <inheritdoc/>
        protected override int GetConfirmedVersion()
        {
            return GlobalStateCache.StateAndMetaData.GlobalVersion;
        }

        /// <inheritdoc/>
        protected override void InitializeConfirmedView(TLogView initialstate)
        {
            GlobalStateCache = new GrainStateWithMetaDataAndETag<TLogView>(initialstate);
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

                    var readState = new GrainStateWithMetaDataAndETag<TLogView>();

                    await globalGrainStorage.ReadStateAsync(grainTypeName, Services.GrainId, readState);

                    GlobalStateCache = readState;

                    Services.Log(LogLevel.Debug, "read success {0}", GlobalStateCache);

                    LastPrimaryIssue.Resolve(Host, Services);

                    break; // successful
                }
                catch (Exception e)
                {
                    LastPrimaryIssue.Record(new ReadFromStateStorageFailed() { Exception = e }, Host, Services);
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

            var state = CopyTentativeState();
            var updates = GetCurrentBatchOfUpdates();
            bool batchsuccessfullywritten = false;

            var nextglobalstate = new GrainStateWithMetaDataAndETag<TLogView>(state);
            nextglobalstate.StateAndMetaData.WriteVector = GlobalStateCache.StateAndMetaData.WriteVector;
            nextglobalstate.StateAndMetaData.GlobalVersion = GlobalStateCache.StateAndMetaData.GlobalVersion + updates.Length;
            nextglobalstate.ETag = GlobalStateCache.ETag;

            var writebit = nextglobalstate.StateAndMetaData.FlipBit(Services.MyClusterId);

            try
            {
                // for manual testing
                //await Task.Delay(5000);

                await globalGrainStorage.WriteStateAsync(grainTypeName, Services.GrainId, nextglobalstate);

                batchsuccessfullywritten = true;

                GlobalStateCache = nextglobalstate;

                Services.Log(LogLevel.Debug, "write ({0} updates) success {1}", updates.Length, GlobalStateCache);

                LastPrimaryIssue.Resolve(Host, Services);
            }
            catch (Exception e)
            {
                LastPrimaryIssue.Record(new UpdateStateStorageFailed() { Exception = e }, Host, Services);
            }

            if (!batchsuccessfullywritten)
            {
                Services.Log(LogLevel.Debug, "write apparently failed {0} {1}", nextglobalstate, LastPrimaryIssue);

                while (true) // be stubborn until we can read what is there
                {

                    await LastPrimaryIssue.DelayBeforeRetry();

                    try
                    {
                        await globalGrainStorage.ReadStateAsync(grainTypeName, Services.GrainId, GlobalStateCache);

                        Services.Log(LogLevel.Debug, "read success {0}", GlobalStateCache);

                        LastPrimaryIssue.Resolve(Host, Services);

                        break;
                    }
                    catch (Exception e)
                    {
                        LastPrimaryIssue.Record(new ReadFromStateStorageFailed() { Exception = e }, Host, Services);
                    }

                    Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);
                }

                // check if last apparently failed write was in fact successful

                if (writebit == GlobalStateCache.StateAndMetaData.GetBit(Services.MyClusterId))
                {
                    GlobalStateCache = nextglobalstate;

                    Services.Log(LogLevel.Debug, "last write ({0} updates) was actually a success {1}", updates.Length, GlobalStateCache);

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
        [GenerateSerializer]
        public sealed class UpdateStateStorageFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"write state to storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }


        /// <summary>
        /// Describes a connection issue that occurred when reading from the primary storage.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public sealed class ReadFromStateStorageFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"read state from storage failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }


        /// <summary>
        /// A notification message sent to remote instances after updating this grain in storage.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        protected internal sealed class UpdateNotificationMessage : INotificationMessage 
        {
            /// <inheritdoc/>
            [Id(0)]
            public int Version { get; set; }

            /// <summary> The cluster that performed the update </summary>
            [Id(1)]
            public string Origin { get; set; }

            /// <summary> The list of updates that were applied </summary>
            [Id(2)]
            public List<TLogEntry> Updates { get; set; }

            /// <summary> The e-tag of the storage after applying the updates</summary>
            [Id(3)]
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

        private readonly SortedList<long, UpdateNotificationMessage> notifications = new SortedList<long,UpdateNotificationMessage>();

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
            while (notifications.Count > 0 && notifications.ElementAt(0).Key < GlobalStateCache.StateAndMetaData.GlobalVersion)
            {
                Services.Log(LogLevel.Debug, "discarding notification {0}", notifications.ElementAt(0).Value);
                notifications.RemoveAt(0);
            }

            // process notifications that reflect next global version
            while (notifications.Count > 0 && notifications.ElementAt(0).Key == GlobalStateCache.StateAndMetaData.GlobalVersion)
            {
                var updateNotification = notifications.ElementAt(0).Value;
                notifications.RemoveAt(0);

                // Apply all operations in pending 
                foreach (var u in updateNotification.Updates)
                    try
                    {
                        Host.UpdateView(GlobalStateCache.StateAndMetaData.State, u);
                    }
                    catch (Exception e)
                    {
                        Services.CaughtUserCodeException("UpdateView", nameof(ProcessNotifications), e);
                    }

                GlobalStateCache.StateAndMetaData.GlobalVersion = updateNotification.Version;

                GlobalStateCache.StateAndMetaData.FlipBit(updateNotification.Origin);

                GlobalStateCache.ETag = updateNotification.ETag;         

                Services.Log(LogLevel.Debug, "notification success ({0} updates) {1}", updateNotification.Updates.Count, GlobalStateCache);
            }

            Services.Log(LogLevel.Trace, "unprocessed notifications in queue: {0}", notifications.Count);

            base.ProcessNotifications();
         
        }


#if DEBUG
        private bool operation_in_progress;
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
