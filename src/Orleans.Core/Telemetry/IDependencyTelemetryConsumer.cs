using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Trace consumer which tracks dependencies.
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITelemetryConsumer" />
    public interface IDependencyTelemetryConsumer : ITelemetryConsumer
    {
        /// <summary>
        /// Traces a command's execution along with its dependency.
        /// </summary>
        /// <param name="dependencyName">Name of the dependency.</param>
        /// <param name="commandName">Name of the command.</param>
        /// <param name="startTime">The start time.</param>
        /// <param name="duration">The duration.</param>
        /// <param name="success">if set to <c>true</c>, the tracked operation was successful.</param>
        void TrackDependency(string dependencyName, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success);
    }
}
