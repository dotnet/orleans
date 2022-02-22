using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Telemetry consumer which tracks a request's execution.
    /// Implements the <see cref="Orleans.Runtime.ITelemetryConsumer" />
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITelemetryConsumer" />
    public interface IRequestTelemetryConsumer : ITelemetryConsumer
    {
        /// <summary>
        /// Tracks a request.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="startTime">The start time.</param>
        /// <param name="duration">The duration.</param>
        /// <param name="responseCode">The response code.</param>
        /// <param name="success">if set to <c>true</c>, the request completed successfully.</param>
        void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success);
    }
}
