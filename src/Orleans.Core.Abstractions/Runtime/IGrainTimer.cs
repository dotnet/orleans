using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a grain timer and its functionality.
    /// </summary>
    internal interface IGrainTimer : IDisposable
    {
        /// <summary>
        /// Starts the timer.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops the timer.
        /// </summary>
        void Stop();

        /// <summary>
        /// Gets the currently executing grain timer task.
        /// </summary>
        /// <returns>The currently executing grain timer task.</returns>
        Task GetCurrentlyExecutingTickTask();
    }
}