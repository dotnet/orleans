using System.Collections.Generic;

namespace Orleans.Runtime
{
    public interface IEventTelemetryConsumer : ITelemetryConsumer
    {
        void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);
    }
}
