using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.EventSourcing.Common
{
    /// <summary>
    /// Base class for notification messages that are sent by log view adaptors to other 
    /// clusters, after updating the log. All subclasses must be serializable.
    /// </summary>
    public interface INotificationMessage : ILogConsistencyProtocolMessage
    {
        ///<summary>The version number.</summary>
        int Version { get; }

        // a log-consistency provider can subclass this to add more information
        // for example, the log entries that were appended, or the view
    }

    /// <summary>A simple notification message containing only the version.</summary>
    [Serializable]
    public class VersionNotificationMessage : INotificationMessage
    {
        /// <inheritdoc/>
        public int Version { get; set;  }
    }


    /// <summary>A notification message containing a batch of notification messages.</summary>
    [Serializable]
    public class BatchedNotificationMessage : INotificationMessage
    {
        /// <summary>The notification messages contained in this batch.</summary>
        public List<INotificationMessage> Notifications { get; set; }

        /// <summary>The version number - for a batch, this is the maximum version contained.</summary>
        public int Version {
            get
            {
                return Notifications.Aggregate(0, (v, m) => Math.Max(v, m.Version));
            }
        }
    }
}
