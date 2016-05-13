using System.Collections.Generic;

namespace Orleans.Runtime
{
    public interface ITraceTelemetryConsumer : ITelemetryConsumer
    {
        void TrackTrace(string message);
        void TrackTrace(string message, Severity severity);
        void TrackTrace(string message, Severity severity, IDictionary<string, string> properties);
        void TrackTrace(string message, IDictionary<string, string> properties);
    }
}
