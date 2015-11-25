using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    public interface IExceptionTelemetryConsumer : ITelemetryConsumer
    {
        void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null);
    }
}
