using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// Interface to be implemented for a storage provider able to read and write Orleans grain state data.
    /// </summary>
    public interface IStorageProvider : IProvider
    {
        /// <summary>Logger used by this storage provider instance.</summary>
        /// <returns>Reference to the Logger object used by this provider.</returns>
        /// <seealso cref="Logger"/>
        Logger Log { get; }

        /// <summary>Read data function for this storage provider instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be populated for this grain.</param>
        /// <returns>Completion promise for the Read operation on the specified grain.</returns>
        Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Write data function for this storage provider instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">State data object to be written for this grain.</param>
        /// <returns>Completion promise for the Write operation on the specified grain.</returns>
        Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);

        /// <summary>Delete / Clear data function for this storage provider instance.</summary>
        /// <param name="grainType">Type of this grain [fully qualified class name]</param>
        /// <param name="grainReference">Grain reference object for this grain.</param>
        /// <param name="grainState">Copy of last-known state data object for this grain.</param>
        /// <returns>Completion promise for the Delete operation on the specified grain.</returns>
        Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState);
    }
}
