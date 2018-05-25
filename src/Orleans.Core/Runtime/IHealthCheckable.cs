using System;

namespace Orleans.Runtime
{
    public interface IHealthCheckable
    {
        /// <summary>
        /// Returns a value indicating the health of this instance.
        /// </summary>
        /// <param name="lastCheckTime">The last time which this instance health was checked.</param>
        /// <returns><see langword="true"/> if the instance is healthy, <see langword="false"/> otherwise.</returns>
        bool CheckHealth(DateTime lastCheckTime);
    }
}