using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.EventSourcing.CustomVersionedStateStorage
{
    /// <summary>
    /// The storage interface exposed by grains that want to use the CustomVersionedStateStorage log-consistency provider
    /// <typeparam name="TState">The type for the state of the grain.</typeparam>
    /// <typeparam name="TDelta">The type for delta objects that represent updates to the state.</typeparam>
    /// </summary>
    public interface ICustomStorageInterface<TState, TDelta>
    {
        /// <summary>
        /// Reads the current state and version from storage
        /// (note that the state object may be mutated by the provider, so it must not be shared).
        /// </summary>
        /// <returns>the version number and a  state object.</returns>
        Task<KeyValuePair<int,TState>> ReadStateFromStorageAsync();

        /// <summary>
        /// Applies the given array of deltas to storage, if the version in storage matches the expected version. 
        /// Otherwise, does nothing. If successful, the version of storage increases by the number of deltas.
        /// </summary>
        /// <returns>true if the deltas were applied, false otherwise</returns>
        Task<bool> ApplyUpdatesToStorageAsync(IReadOnlyList<TDelta> updates, int expectedversion);
    }

}
