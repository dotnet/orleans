using System;

namespace Orleans.Runtime
{
    public interface IDependencyTelemetryConsumer : ITelemetryConsumer
    {
        void TrackDependency(string dependencyName, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success);
    }
}
