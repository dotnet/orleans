using System.Collections.Generic;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents the collection of clients which are currently connected to this gateway.
    /// </summary>
    internal interface IConnectedClientCollection
    {
        /// <summary>
        /// The monotonically increasing version of the collection, which can be used for change notification.
        /// </summary>
        long Version { get; }

        /// <summary>
        /// Gets the collection of ids for the connected clients.
        /// </summary>
        List<GrainId> GetConnectedClientIds();
    }

    internal sealed class EmptyConnectedClientCollection : IConnectedClientCollection
    {
        public long Version => 0;

        public List<GrainId> GetConnectedClientIds() => new List<GrainId>(0);
    }
}
