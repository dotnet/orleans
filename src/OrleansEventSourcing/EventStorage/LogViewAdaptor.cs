using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.EventSourcing.Common;
using Orleans.LogConsistency;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.EventSourcing.EventStorage
{

    /// <summary>
    /// A log view adaptor that is based on an event store
    /// </summary>
    /// <typeparam name="TState">The type of the log view, or grain state.</typeparam>
    /// <typeparam name="TEvent">The type of the events (usually just object)</typeparam>
    internal class EventStoreLogViewAdaptor<TState, TEvent> : PrimaryBasedLogViewAdaptor<TState, TEvent, SubmissionEntryWithGuid<TEvent>>
        where TState : class, new() where TEvent : class
    {
        // the provider that created this adaptor
        private LogConsistencyProvider provider;

        // the confirmed state (= what was read from storage most recently)
        private TState confirmedState;
        private int confirmedVersion;

        // the handle to the event stream returned by the event store provider
        private IEventStreamHandle<TEvent> eventStream;

        // must be constructed lazily because grain reference is not available at construction time
        private IEventStreamHandle<TEvent> EventStream { get {
                return (eventStream ??
                   (eventStream = provider.EventStore.GetEventStreamHandle<TEvent>(
                       provider.EventStore.DefaultStreamName(Host.GetType(), Services.GrainReference))));
            }
        }

        public EventStoreLogViewAdaptor(
            ILogViewAdaptorHost<TState, TEvent> host, 
            LogConsistencyProvider provider,
            TState initialState, 
            ILogConsistencyProtocolServices services)  : base(host, initialState, services)
        {
            this.provider = provider;

            if (this.provider == null)
            {
                throw new ArgumentException("provider must be an event store", nameof(provider));
            }
        }

        // streamname must be constructed lazily because grain reference is not available at construction time
        private string streamName
        {
            get
            {
                return EventStream.StreamName;
            }
        }

        protected override TState LastConfirmedView()
        {
            return confirmedState;
        }

        protected override int GetConfirmedVersion()
        {
            return confirmedVersion;
        }

        protected override void InitializeConfirmedView(TState initialState)
        {
            confirmedState = initialState;
            confirmedVersion = 0;
        }

        public override async Task<IReadOnlyList<TEvent>> RetrieveLogSegment(int fromVersion, int toVersion)
        {
            var response = await EventStream.Load(fromVersion, toVersion);

            if (response.ToVersion != toVersion || response.FromVersion != fromVersion || response.Events.Count != (toVersion - fromVersion))
            {
                throw new NotSupportedException("the event store returned a different segment than was requested");
            }

            return response.Events.Select(kvp => (TEvent) kvp.Value).ToList();
        }

        /// <summary> Creates a submission entry used to track this event </summary>
        protected override SubmissionEntryWithGuid<TEvent> MakeSubmissionEntry(TEvent entry)
        {
            return new SubmissionEntryWithGuid<TEvent>()
            {
                Entry = entry,

                // for any events that do not already have a Guid, create one
                Guid = (entry as IEventWithGuid)?.Id ?? Guid.NewGuid()
            };
        }

        // Read the latest primary state, retrying until successful
        protected override async Task ReadAsync()
        {
            await ReadAsyncInternal();
        }

        // Read the latest primary state, retrying until successful
        // if a guid is specified, while doing so, look for it in the stream
        private async Task<bool> ReadAsyncInternal(Guid? lookFor = null)
        {
            bool guid_was_seen = false;

            EventStreamSegment<TEvent> response;

            while (true)
            {
                try
                {
                    Services.Log(Severity.Verbose, "Read issued for position={0}", confirmedVersion);

                    response = await EventStream.Load(confirmedVersion);

                    break;
                }
                catch (Exception e)
                {
                    LastPrimaryIssue.Record(new LoadEventsFailed() { Exception = e }, Host, Services);

                    await LastPrimaryIssue.DelayBeforeRetry();
                }
            }

            LastPrimaryIssue.Resolve(Host, Services);

            Services.Log(Severity.Verbose, "Read success from={0} to={1}", response.FromVersion, response.ToVersion);

            // check validity of response
            if (response.ToVersion < response.FromVersion || response.Events == null || response.Events.Count != (response.ToVersion - response.FromVersion))
            {
                throw new OrleansException($"storage returned invalid response");
            }
            if (response.FromVersion != confirmedVersion)
            {
                throw new OrleansException($"storage returned wrong events, starting at {response.FromVersion} instead of {confirmedVersion}.");
            }
            if (response.ToVersion < confirmedVersion)
            {
                throw new OrleansException($"storage returned stale version {response.ToVersion}, expected at least {confirmedVersion}.");
            }

            // construct state from stream of events
            foreach (var item in response.Events)
            {
                if (lookFor.HasValue && lookFor.Value.Equals(item.Key))
                {
                    guid_was_seen = true;
                }

                try
                {
                    Host.UpdateView(confirmedState, item.Value);
                }
                catch (Exception e)
                {
                    Services.CaughtUserCodeException("UpdateView", nameof(ReadAsyncInternal), e);
                }

                confirmedVersion++;
            }

            return guid_was_seen;
        }

        // write entries in pending queue as a batch. 
        // Retry until outcome has been conclusively determined.
        // Return 0 if no updates were written, or the size of the whole batch if updates were written.
        protected override async Task<int> WriteAsync()
        {
            var updates = GetCurrentBatchOfUpdates()
                .Select(u => new KeyValuePair<Guid, TEvent>(u.Guid, u.Entry))
                .ToList().AsReadOnly();

            // record one of the guids in the batch, we will use it for idempotence check on failures
            Guid guidForIdempotenceCheck = updates[0].Key;

            Services.Log(Severity.Verbose, "Write batch size={0} guid={1}", updates.Count, guidForIdempotenceCheck);

            bool success;

            try
            {
                success = await EventStream.Append(
                       updates,
                       confirmedVersion
                    );

                if (!success)
                {
                    LastPrimaryIssue.Record(new AppendEventsFailed() { IsOptimisticConcurrencyConflict = true }, Host, Services);
                }
                else
                {
                    LastPrimaryIssue.Resolve(Host, Services);

                    Services.Log(Severity.Verbose, "Write successful");

                    // update confirmed state
                    foreach (var item in updates)
                    {
                        try
                        {
                            Host.UpdateView(confirmedState, (TEvent) item.Value);
                        }
                        catch (Exception e)
                        {
                            Services.CaughtUserCodeException("UpdateView", nameof(WriteAsync), e);
                        }

                        confirmedVersion++;
                    }
                }
            }
            catch (Exception e)
            {
                success = false;
                LastPrimaryIssue.Record(new AppendEventsFailed() { Exception = e, IsOptimisticConcurrencyConflict = false }, Host, Services);
            }

            if (!success)
            {
                Services.Log(Severity.Verbose, "Write apparently unsuccessful");

                // an exception was thrown, or there was a concurrency failure.
                // In either case, we need to read the latest state.
                // if we see the guid in there, we know this append did actually succeeded
                success = await ReadAsyncInternal(guidForIdempotenceCheck);

                if (success)
                {
                    Services.Log(Severity.Verbose, "Write actually succeeded after all");
                }
            }

            // let other clusters know about the new events
            // (note: BroadcastNotification includes a batching optimization and reverts to 
            // version-number notification messages if there are too many events)
            if (success)
            {
                BroadcastNotification(new UpdateNotificationMessage()
                {
                    Version = confirmedVersion,
                    Updates = updates,
                });
            }

            return success ? updates.Count : 0;
        }



        /// <summary>
        /// Describes a connection issue that occurred when loading events from the event store.
        /// </summary>
        [Serializable]
        public class LoadEventsFailed : PrimaryOperationFailed
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return $"load stream failed: caught {Exception.ToString()}";
            }
        }


        /// <summary>
        /// Describes a connection issue that occurred when appending events to the event store.
        /// </summary>
        [Serializable]
        public class AppendEventsFailed : PrimaryOperationFailed
        {
            /// <summary>
            /// Whether this failure was caused by an optimistic-concurrency conflict
            /// </summary>
            public bool IsOptimisticConcurrencyConflict { get; set; }

            /// <inheritdoc/>
            public override string ToString()
            {
                return $"append to stream failed: caught {Exception.ToString()}";
            }
        }


        /// <summary>
        /// A notification message that is sent to remote instances of this grain after the primary has been
        /// updated, to let them know the latest version. Contains all the updates that were applied.
        /// </summary>
        [Serializable]
        private class UpdateNotificationMessage : INotificationMessage
        {
            /// <inheritdoc/>
            public int Version { get; set; }

            /// <summary> The list of updates that were applied. </summary>
            public IReadOnlyList<KeyValuePair<Guid,TEvent>> Updates { get; set; }

            /// <summary>
            /// A representation of this notification message suitable for tracing.
            /// </summary>
            public override string ToString()
            {
                return string.Format("v{0} ({1} updates, last={2})", Version, Updates.Count, Updates.Last().Key);
            }
        }

        private SortedList<long, UpdateNotificationMessage> notifications = new SortedList<long, UpdateNotificationMessage>();

        /// <inheritdoc/>
        protected override void OnNotificationReceived(INotificationMessage payload)
        {
            var um = payload as UpdateNotificationMessage;
            if (um != null)
            {
                notifications.Add(um.Version - um.Updates.Count, um);
            }
            else
            {
                // this is a type of notification handled by the base notification mechanism
                base.OnNotificationReceived(payload);
            }
        }

        /// <inheritdoc/>
        protected override void ProcessNotifications()
        {

            // discard notifications that are behind our already confirmed state
            while (notifications.Count > 0 && notifications.ElementAt(0).Key < confirmedVersion)
            {
                Services.Log(Severity.Verbose, "discarding notification {0}", notifications.ElementAt(0).Value);
                notifications.RemoveAt(0);
            }

            // process notifications that reflect next global version
            while (notifications.Count > 0 && notifications.ElementAt(0).Key == confirmedVersion)
            {
                var updatenotification = notifications.ElementAt(0).Value;
                notifications.RemoveAt(0);

                // Apply all operations in pending 
                foreach (var u in updatenotification.Updates)
                    try
                    {
                        Host.UpdateView(confirmedState, u.Value);
                    }
                    catch (Exception e)
                    {
                        Services.CaughtUserCodeException("UpdateView", nameof(ProcessNotifications), e);
                    }

                confirmedVersion = updatenotification.Version;

                Services.Log(Severity.Verbose, "notification success ({0} updates) v{1}", updatenotification.Updates.Count, confirmedVersion);
            }

            Services.Log(Severity.Verbose2, "unprocessed notifications in queue: {0}", notifications.Count);

            base.ProcessNotifications();
        }
    }
}
