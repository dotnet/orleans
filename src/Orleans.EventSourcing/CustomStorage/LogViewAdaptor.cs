using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Storage;
using Orleans.EventSourcing.Common;

namespace Orleans.EventSourcing.CustomStorage
{
    /// <summary>
    /// A log consistency adaptor that uses the user-provided storage interface <see cref="ICustomStorageInterface{T,E}"/>. 
    /// This interface must be implemented by any grain that uses this log view adaptor.
    /// </summary>
    /// <typeparam name="TLogView">log view type</typeparam>
    /// <typeparam name="TLogEntry">log entry type</typeparam>
    internal class CustomStorageAdaptor<TLogView, TLogEntry> : PrimaryBasedLogViewAdaptor<TLogView, TLogEntry, SubmissionEntry<TLogEntry>>
        where TLogView : class, new()
        where TLogEntry : class
    {
        /// <summary>
        /// Initialize a new instance of CustomStorageAdaptor class
        /// </summary>
        public CustomStorageAdaptor(ILogViewAdaptorHost<TLogView, TLogEntry> host, TLogView initialState,
            ILogConsistencyProtocolServices services, string primaryCluster)
            : base(host, initialState, services)
        {
            if (!(host is ICustomStorageInterface<TLogView, TLogEntry>))
                throw new BadProviderConfigException("Must implement ICustomStorageInterface<TLogView,TLogEntry> for CustomStorageLogView provider");
            this.primaryCluster = primaryCluster;
        }

        private string primaryCluster;

        private TLogView cached;
        private int version;

        /// <inheritdoc/>
        protected override TLogView LastConfirmedView()
        {
            return cached;
        }

        /// <inheritdoc/>
        protected override int GetConfirmedVersion()
        {
            return version;
        }

        /// <inheritdoc/>
        protected override void InitializeConfirmedView(TLogView initialstate)
        {
            cached = initialstate;
            version = 0;
        }

        /// <inheritdoc/>
        protected override bool SupportSubmissions
        {
            get
            {
                return true;
            }
        }

        /// <inheritdoc/>
        protected override SubmissionEntry<TLogEntry> MakeSubmissionEntry(TLogEntry entry)
        {
           // no special tagging is required, thus we create a plain submission entry
           return new SubmissionEntry<TLogEntry>() { Entry = entry };
        }

        [Serializable]
        [GenerateSerializer]
        private class ReadRequest : ILogConsistencyProtocolMessage
        {
            [Id(0)]
            public int KnownVersion { get; set; }
        }
        [Serializable]
        [GenerateSerializer]
        private class ReadResponse<ViewType> : ILogConsistencyProtocolMessage
        {
            [Id(0)]
            public int Version { get; set; }

            [Id(1)]
            public ViewType Value { get; set; }
        }

        /// <inheritdoc/>
        protected override Task<ILogConsistencyProtocolMessage> OnMessageReceived(ILogConsistencyProtocolMessage payload)
        {
            var request = (ReadRequest) payload;

            var response = new ReadResponse<TLogView>() { Version = version };

            // optimization: include value only if version is newer
            if (version > request.KnownVersion)
                response.Value = cached;

            return Task.FromResult<ILogConsistencyProtocolMessage>(response);
        }

        /// <inheritdoc/>
        protected override async Task ReadAsync()
        {
            enter_operation("ReadAsync");

            while (true)
            {
                try
                {
                    // read from storage
                    var result = await ((ICustomStorageInterface<TLogView, TLogEntry>)Host).ReadStateFromStorage();
                    version = result.Key;
                    cached = result.Value;

                    Services.Log(LogLevel.Debug, "read success v{0}", version);

                    LastPrimaryIssue.Resolve(Host, Services);

                    break; // successful
                }
                catch (Exception e)
                {
                    // unwrap inner exception that was forwarded - helpful for debugging
                    if ((e as ProtocolTransportException)?.InnerException != null)
                        e = ((ProtocolTransportException)e).InnerException;

                    LastPrimaryIssue.Record(new ReadFromPrimaryFailed() { Exception = e }, Host, Services);
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

            var updates = GetCurrentBatchOfUpdates().Select(submissionentry => submissionentry.Entry).ToList();
            bool writesuccessful = false;
            bool transitionssuccessful = false;

            try
            {
                writesuccessful = await ((ICustomStorageInterface<TLogView,TLogEntry>) Host).ApplyUpdatesToStorage(updates, version);

                LastPrimaryIssue.Resolve(Host, Services);
            }
            catch (Exception e)
            {
                // unwrap inner exception that was forwarded - helpful for debugging
                if ((e as ProtocolTransportException)?.InnerException != null)
                    e = ((ProtocolTransportException)e).InnerException;

                LastPrimaryIssue.Record(new UpdatePrimaryFailed() { Exception = e }, Host, Services);
            }

            if (writesuccessful)
            {
                Services.Log(LogLevel.Debug, "write ({0} updates) success v{1}", updates.Count, version + updates.Count);

                // now we update the cached state by applying the same updates
                // in case we encounter any exceptions we will re-read the whole state from storage
                try
                {
                    foreach (var u in updates)
                    {
                        version++;
                        Host.UpdateView(this.cached, u);
                    }

                    transitionssuccessful = true;
                }
                catch (Exception e)
                {
                    Services.CaughtUserCodeException("UpdateView", nameof(WriteAsync), e);
                }
            }

            if (!writesuccessful || !transitionssuccessful)
            {
                Services.Log(LogLevel.Debug, "{0} failed {1}", writesuccessful ? "transitions" : "write", LastPrimaryIssue);

                while (true) // be stubborn until we can re-read the state from storage
                {
                    await LastPrimaryIssue.DelayBeforeRetry();

                    try
                    {
                        var result = await ((ICustomStorageInterface<TLogView, TLogEntry>)Host).ReadStateFromStorage();
                        version = result.Key;
                        cached = result.Value;

                        Services.Log(LogLevel.Debug, "read success v{0}", version);

                        LastPrimaryIssue.Resolve(Host, Services);

                        break;
                    }
                    catch (Exception e)
                    {
                        // unwrap inner exception that was forwarded - helpful for debugging
                        if ((e as ProtocolTransportException)?.InnerException != null)
                            e = ((ProtocolTransportException)e).InnerException;

                        LastPrimaryIssue.Record(new ReadFromPrimaryFailed() { Exception = e }, Host, Services);
                    }

                    Services.Log(LogLevel.Debug, "read failed {0}", LastPrimaryIssue);
                }
            }

            exit_operation("WriteAsync");

            return writesuccessful ? updates.Count : 0;
        }

        /// <summary>
        /// Describes a connection issue that occurred when updating the primary storage.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public class UpdatePrimaryFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"update primary failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }


        /// <summary>
        /// Describes a connection issue that occurred when reading from the primary storage.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public class ReadFromPrimaryFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"read from primary failed: caught {Exception.GetType().Name}: {Exception.Message}";
            }
        }


        /// <summary>
        /// A notification message that is sent to remote instances of this grain after the primary has been
        /// updated, to let them know the latest version. Contains all the updates that were applied.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        protected class UpdateNotificationMessage : INotificationMessage
        {
            /// <inheritdoc/>
            [Id(0)]
            public int Version { get; set; }

            /// <summary> The list of updates that were applied. </summary>
            [Id(1)]
            public List<TLogEntry> Updates { get; set; }

            /// <summary>
            /// A representation of this notification message suitable for tracing.
            /// </summary>
            public override string ToString()
            {
                return string.Format("v{0} ({1} updates)", Version, Updates.Count);
            }
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
            while (notifications.Count > 0 && notifications.ElementAt(0).Key < version)
            {
                Services.Log(LogLevel.Debug, "discarding notification {0}", notifications.ElementAt(0).Value);
                notifications.RemoveAt(0);
            }

            // process notifications that reflect next global version
            while (notifications.Count > 0 && notifications.ElementAt(0).Key == version)
            {
                var updatenotification = notifications.ElementAt(0).Value;
                notifications.RemoveAt(0);

                // Apply all operations in pending 
                foreach (var u in updatenotification.Updates)
                    try
                    {
                        Host.UpdateView(cached, u);
                    }
                    catch (Exception e)
                    {
                        Services.CaughtUserCodeException("UpdateView", nameof(ProcessNotifications), e);
                    }

                version = updatenotification.Version;

                Services.Log(LogLevel.Debug, "notification success ({0} updates) v{1}", updatenotification.Updates.Count, version);
            }

            Services.Log(LogLevel.Trace, "unprocessed notifications in queue: {0}", notifications.Count);

            base.ProcessNotifications();
        
        }

        [Conditional("DEBUG")]
        private void enter_operation(string name)
        {
            Services.Log(LogLevel.Trace, "/-- enter {0}", name);
        }

        [Conditional("DEBUG")]
        private void exit_operation(string name)
        {
            Services.Log(LogLevel.Trace, "\\-- exit {0}", name);
        }

    }
}
