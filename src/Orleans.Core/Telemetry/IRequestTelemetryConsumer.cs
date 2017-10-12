using System;

namespace Orleans.Runtime
{
    public interface IRequestTelemetryConsumer : ITelemetryConsumer
    {
        void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success);
    }
}
