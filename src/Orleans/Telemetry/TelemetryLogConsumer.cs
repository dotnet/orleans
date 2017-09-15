using Orleans.Runtime;
using System;
using System.Net;

namespace Orleans.Extensions.Logging
{
    /// <summary>
    /// Forward trace log calls to the telemetry abstractions. This will be replaced by an ILoggerProvider in the future.
    /// </summary>
    public class TelemetryLogConsumer : ILogConsumer, IFlushableLogConsumer, ICloseableLogConsumer
    {
        private readonly ITelemetryClient telemetryClient;

        public TelemetryLogConsumer(ITelemetryClient telemetryClient)
        {
            this.telemetryClient = telemetryClient;
        }


        public void Log(Severity severity, LoggerType loggerType, string caller, string message, IPEndPoint myIPEndPoint, Exception exception, int eventCode = 0)
        {
#pragma warning disable CS0618 // Type or member is obsolete

            if (exception != null)
            {
                this.telemetryClient.TrackException(exception);
            }

            var logMessage = TraceParserUtils.FormatLogMessage(DateTime.UtcNow, severity, loggerType, caller, message, myIPEndPoint, exception, eventCode);
            this.telemetryClient.TrackTrace(logMessage, severity);

#pragma warning restore CS0618 // Type or member is obsolete
        }

        public void Close()
        {
            this.telemetryClient.Close();
        }

        public void Flush()
        {
            this.telemetryClient.Flush();
        }
    }
}
