
using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Grain that tracks event-storage exceptions to be injected.
    /// </summary>
    public interface IEventStorageFaultGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Adds one or more exceptions to throw when the referenced grain reads state from a event storage provider
        /// </summary>
        /// <param name="exceptions">A sequence of exceptions, or null for throw-no-exception.</param>
        Task Add(params Exception[] exceptions);

        /// <summary>
        /// Clears the queued exceptions.
        /// </summary>
        Task Clear();

        /// <summary>
        /// Returns the next exception to throw in the sequence.
        /// </summary>
        /// <returns>the next exception, or null if no exception should be thrown.</returns>
        Task Next();   
    }
}
