using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// This interface is for use with the Orleans timers.
    /// </summary>
    internal interface ITimebound
    {
        /// <summary>
        /// This method is called by the timer when the time out is reached.
        /// </summary>
        void OnTimeout();
        TimeSpan RequestedTimeout();
    }
}