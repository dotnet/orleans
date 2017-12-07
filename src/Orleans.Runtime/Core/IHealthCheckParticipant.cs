using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Interface for health check participants.
    /// </summary>
    public interface IHealthCheckParticipant
    {
        /// <summary>
        /// Returns a value indicating the health of this instance.
        /// </summary>
        /// <param name="lastCheckTime">The last time which this participant's health was checked.</param>
        /// <returns><see langword="true"/> if the participant is healthy, <see langword="false"/> otherwise.</returns>
        bool CheckHealth(DateTime lastCheckTime);
    }
}

