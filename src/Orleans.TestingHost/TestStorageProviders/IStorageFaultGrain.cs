
using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Grain that tracks storage exceptions to be injected.
    /// </summary>
    public interface IStorageFaultGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain reads state from a storage provider
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>Task.</returns>
        Task AddFaultOnRead(GrainReference grainReference, Exception exception);

        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain writes state to a storage provider
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>Task.</returns>
        Task AddFaultOnWrite(GrainReference grainReference, Exception exception);

        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain clears state in a storage provider
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>Task.</returns>
        Task AddFaultOnClear(GrainReference grainReference, Exception exception);

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for reading.
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <returns>Task.</returns>
        Task OnRead(GrainReference grainReference);

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for writing.
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <returns>Task.</returns>
        Task OnWrite(GrainReference grainReference);

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for clearing state.
        /// </summary>
        /// <param name="grainReference">The grain reference.</param>
        /// <returns>Task.</returns>
        Task OnClear(GrainReference grainReference);
    }
}
