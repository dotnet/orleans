using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Interface for services which can be probed for health status.
    /// </summary>
    public interface IHealthCheckable
    {
        /// <summary>
        /// Returns a value indicating the health of this instance.
        /// </summary>
        /// <param name="lastCheckTime">The last time which this instance health was checked.</param>
        /// <param name="reason">If this method returns <see langword="false"/>, this parameter will describe the reason for that verdict.</param>
        /// <returns><see langword="true"/> if the instance is healthy, <see langword="false"/> otherwise.</returns>
        bool CheckHealth(DateTime lastCheckTime, out string reason);
    }
}