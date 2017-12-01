using Orleans.LogConsistency;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.Common
{

    /// <summary>
    /// Helper class for tracking notifications that a grain sends to other clusters after updating the log.
    /// </summary>
    internal class NotificationTracker
    {
        internal ILogConsistencyProtocolServices services;
        internal IConnectionIssueListener listener;
        internal int maxNotificationBatchSize;

        private Dictionary<string, NotificationWorker> sendWorkers;

        public NotificationTracker(ILogConsistencyProtocolServices services, IEnumerable<string> remoteInstances, int maxNotificationBatchSize, IConnectionIssueListener listener)
        {
            this.services = services;
            this.listener = listener;
            sendWorkers = new Dictionary<string, NotificationWorker>();
            this.maxNotificationBatchSize = maxNotificationBatchSize;

            foreach (var x in remoteInstances)
            {
                services.Log(Severity.Verbose, "Now sending notifications to {0}", x);
                sendWorkers.Add(x, new NotificationWorker(this, x));
            }
        }

        public void BroadcastNotification(INotificationMessage msg, string exclude = null)
        {
            foreach (var kvp in sendWorkers)
            {
                if (kvp.Key != exclude)
                {
                    var w = kvp.Value;
                    w.Enqueue(msg);
                }
            }
        }

        /// <summary>
        /// returns unresolved connection issues observed by the workers
        /// </summary>
        public IEnumerable<ConnectionIssue> UnresolvedConnectionIssues
        {
            get
            {
                return sendWorkers.Values.Select(sw => sw.LastConnectionIssue.Issue).Where(i => i != null);
            }
        }

        /// <summary>
        /// Update the multicluster configuration (change who to send notifications to)
        /// </summary>
        public void UpdateNotificationTargets(IReadOnlyList<string> remoteInstances)
        {
            var removed = sendWorkers.Keys.Except(remoteInstances);
            foreach (var x in removed)
            {
                services.Log(Severity.Verbose, "No longer sending notifications to {0}", x);
                sendWorkers[x].Done = true;
                sendWorkers.Remove(x);
            }

            var added = remoteInstances.Except(sendWorkers.Keys);
            foreach (var x in added)
            {
                if (x != services.MyClusterId)
                {
                    services.Log(Severity.Verbose, "Now sending notifications to {0}", x);
                    sendWorkers.Add(x, new NotificationWorker(this, x));
                }
            }
        }


        public enum NotificationQueueState
        {
            Empty,
            Single,
            Batch,
            VersionOnly
        }

        /// <summary>
        /// Asynchronous batch worker that sends notfications to a particular cluster.
        /// </summary>
        public class NotificationWorker : BatchWorker
        {
            private NotificationTracker tracker;
            private string clusterId;

            /// <summary>
            /// Queue messages
            /// </summary>
            private INotificationMessage QueuedMessage = null;
            /// <summary>
            /// Queue state
            /// </summary>
            private NotificationQueueState QueueState = NotificationQueueState.Empty;
            /// <summary>
            /// Last exception
            /// </summary>
            public RecordedConnectionIssue LastConnectionIssue;
            /// <summary>
            /// Is current task done or not
            /// </summary>
            public bool Done;

            /// <summary>
            /// Initialize a new instance of NotificationWorker class
            /// </summary>
            public NotificationWorker(NotificationTracker tracker, string clusterId)
            {
                this.tracker = tracker;
                this.clusterId = clusterId;
            }

            /// <summary>
            /// Enqueue method
            /// </summary>
            /// <param name="msg">The message to enqueue</param>
            public void Enqueue(INotificationMessage msg)
            {
                switch (QueueState)
                {
                    case (NotificationQueueState.Empty):
                        {
                            QueuedMessage = msg;
                            QueueState = NotificationQueueState.Single;
                            break;
                        }
                    case (NotificationQueueState.Single):
                        {
                            var m = new List<INotificationMessage>();
                            m.Add(QueuedMessage);
                            m.Add(msg);
                            QueuedMessage = new BatchedNotificationMessage() { Notifications = m };
                            QueueState = NotificationQueueState.Batch;
                            break;
                        }
                    case (NotificationQueueState.Batch):
                        {
                            var batchmsg = (BatchedNotificationMessage)QueuedMessage;
                            if (batchmsg.Notifications.Count < tracker.maxNotificationBatchSize)
                            {
                                batchmsg.Notifications.Add(msg);
                                break;
                            }
                            else
                            {
                                // keep only a version notification
                                QueuedMessage = new VersionNotificationMessage() { Version = msg.Version };
                                QueueState = NotificationQueueState.VersionOnly;
                                break;
                            }
                        }
                    case (NotificationQueueState.VersionOnly):
                        {
                            ((VersionNotificationMessage)QueuedMessage).Version = msg.Version;
                            QueueState = NotificationQueueState.VersionOnly;
                            break;
                        }
                }
                Notify();
            }



            protected override async Task Work()
            {
                if (Done) return; // has been terminated - now garbage.

                // if we had issues sending last time, wait a bit before retrying
                await LastConnectionIssue.DelayBeforeRetry();

                // take all of current queue
                var msg = QueuedMessage;
                var state = QueueState;

                if (state == NotificationQueueState.Empty)
                    return;

                // queue is now empty (and may grow while this worker is doing awaits)
                QueuedMessage = null;
                QueueState = NotificationQueueState.Empty;

                // try to send it
                try
                {
                    await tracker.services.SendMessage(msg, clusterId);

                    // notification was successful
                    tracker.services.Log(Severity.Verbose, "Sent notification to cluster {0}: {1}", clusterId, msg);

                    LastConnectionIssue.Resolve(tracker.listener, tracker.services);
                }
                catch (Exception e)
                {
                    tracker.services.Log(Severity.Info, "Could not send notification to cluster {0}: {1}", clusterId, e);

                    LastConnectionIssue.Record(
                        new NotificationFailed() { RemoteCluster = clusterId, Exception = e },
                        tracker.listener, tracker.services);

                    // next time, send only version (this is an optimization that 
                    // avoids the queueing and sending of lots of data when there are errors observed)
                    QueuedMessage = new VersionNotificationMessage() { Version = msg.Version };
                    QueueState = NotificationQueueState.VersionOnly;
                    Notify();
                }
            }
        }
    }
}
