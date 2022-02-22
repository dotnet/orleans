using System.Collections.Generic;
using System.Diagnostics;

namespace Orleans.Runtime
{
    /// <summary>
    /// <see cref="ITelemetryConsumer"/> which writes output to <see cref="System.Diagnostics.Trace"/>.
    /// Implements the <see cref="Orleans.Runtime.ITraceTelemetryConsumer" />
    /// </summary>
    /// <seealso cref="Orleans.Runtime.ITraceTelemetryConsumer" />
    public class TraceTelemetryConsumer : ITraceTelemetryConsumer
    {
        /// <inheritdoc/>
        public void TrackTrace(string message)
        {
            Trace.TraceInformation(message);
        }

        /// <inheritdoc/>
        public void TrackTrace(string message, IDictionary<string, string> properties)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));            
        }

        /// <inheritdoc/>
        public void TrackTrace(string message, Severity severity)
        {
            switch (severity)
            {
                case Severity.Error:
                    Trace.TraceError(message);
                    break;
                case Severity.Info:
                    Trace.TraceInformation(message);
                    break;
                case Severity.Verbose:
                case Severity.Verbose2:
                case Severity.Verbose3:
                    Trace.WriteLine(message);
                    break;
                case Severity.Warning:
                    Trace.TraceWarning(message);
                    break;
                case Severity.Off:
                    return;
            }
            Trace.Flush();
        }

        /// <inheritdoc/>
        public void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties), severity);
        }

        /// <inheritdoc/>
        public void Flush()
        {
            Trace.Flush();
        }

        /// <inheritdoc/>
        public void Close()
        {
            // We are not closing Trace here, since Orleans does not own the configured TraceListeners.
            // Closing here cause a possible failure for any further Trace method calls outside of Orleans too,
            // which can lead to unpredicted results, like in case of Azure an ObjectDisposedException.
        }
    }
}
