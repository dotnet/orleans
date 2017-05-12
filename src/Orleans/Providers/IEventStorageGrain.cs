using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain interface for internal memory event-storage grain used by Orleans in-memory event-storage provider.
    /// </summary>
    public interface IMemoryEventStorageGrain : IGrainWithIntegerKey 
    {
        /// <inheritdoc cref="IEventStreamHandle"/>
        Task<int> GetVersion(string streamName);

        /// <inheritdoc cref="IEventStreamHandle"/>
        Task<EventStreamSegment<object>> Load(string streamName, int startAtVersion = 0, int? endAtVersion = null);

        /// <inheritdoc cref="IEventStreamHandle"/>
        Task<bool> Append(string streamName, IEnumerable<KeyValuePair<Guid, object>> events, int? expectedVersion = null);

        /// <inheritdoc cref="IEventStreamHandle"/>
        Task<bool> Delete(string streamName, int? expectedVersion = null);
    }
}
