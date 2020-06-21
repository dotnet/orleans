using System;
using System.Threading.Tasks;
using Orleans.Runtime.Scheduler;
using Orleans.Streams;

namespace Orleans.Runtime
{
    /// <summary>
    /// Runtime client methods accessible on silos.
    /// </summary>
    internal interface ISiloRuntimeClient : IRuntimeClient
    {
        /// <summary>
        /// Gets the stream directory.
        /// </summary>
        /// <returns>The stream directory.</returns>
        StreamDirectory GetStreamDirectory();
        
        void DeactivateOnIdle(ActivationId id);

        OrleansTaskScheduler Scheduler { get; }
        Task Invoke(IGrainContext target, Message message);
    }
}