
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
        /// <param name="grainReference"></param>
        /// <param name="exception"></param>
        /// <returns></returns>
        Task AddFaultOnRead(GrainReference grainReference, Exception exception);
        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain writes state to a storage provider
        /// </summary>
        /// <param name="grainReference"></param>
        /// <param name="exception"></param>
        /// <returns></returns>
        Task AddFaultOnWrite(GrainReference grainReference, Exception exception);
        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain clears state in a storage provider
        /// </summary>
        /// <param name="grainReference"></param>
        /// <param name="exception"></param>
        /// <returns></returns>
        Task AddFaultOnClear(GrainReference grainReference, Exception exception);

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for reading.
        /// </summary>
        /// <param name="grainReference"></param>
        /// <returns></returns>
        Task OnRead(GrainReference grainReference);
        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for writing.
        /// </summary>
        /// <param name="grainReference"></param>
        /// <returns></returns>
        Task OnWrite(GrainReference grainReference);
        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for clearing state.
        /// </summary>
        /// <param name="grainReference"></param>
        /// <returns></returns>
        Task OnClear(GrainReference grainReference);
    }
}
