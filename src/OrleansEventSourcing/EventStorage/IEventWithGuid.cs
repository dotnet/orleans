using Orleans.EventSourcing.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.EventStorage
{
    /// <summary>
    /// An interface marking events that have a Guid property identifying them.
    /// </summary>
    public interface IEventWithGuid
    {
        /// <summary>
        /// A unique identifier for this event.
        /// </summary>
        Guid Id { get; }
    }

    /// <summary>
    /// This class is used by the log-view adaptor to store a guid together with a submitted event.
    /// </summary>
    /// <typeparam name="TLogEntry"></typeparam>
    internal class SubmissionEntryWithGuid<TLogEntry> : SubmissionEntry<TLogEntry>
    {
        /// <summary> A Guid for this event. </summary>
        public Guid Guid;
    }
}
