using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain interface for internal memory event-storage grain used by Orleans in-memory event-storage provider.
    /// </summary>
    /// <typeparam name="TEvent">The base class for the events</typeparam>
    public interface IMemoryEventStorageGrain<TEvent> : IGrainWithIntegerKey 
    {
        /// <inheritdoc cref="IEventStreamHandle{TEvent}"/>
        Task<int> GetVersion(string streamName);

        /// <inheritdoc cref="IEventStreamHandle{TEvent}"/>
        Task<EventStreamSegment<TEvent>> Load(string streamName, int startAtVersion = 0, int? endAtVersion = null);

        /// <inheritdoc cref="IEventStreamHandle{TEvent}"/>
        Task<bool> Append(string streamName, IEnumerable<KeyValuePair<Guid, TEvent>> events, int? expectedVersion = null);

        /// <inheritdoc cref="IEventStreamHandle{TEvent}"/>
        Task<bool> Delete(string streamName, int? expectedVersion = null);
    }
}
