using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    public class ConsoleTelemetryConsumer : ITraceTelemetryConsumer, IExceptionTelemetryConsumer
    {
        public void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
            ConsoleText.WriteError(TraceParserUtils.PrintProperties(exception.Message, properties), exception);
        }

        public void TrackTrace(string message)
        {
            ConsoleText.WriteLine(message);
        }

        public void TrackTrace(string message, IDictionary<string, string> properties = null)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));
        }

        public void TrackTrace(string message, Severity severity)
        {
            switch (severity)
            {
                case Severity.Error:
                    ConsoleText.WriteError(message);
                    break;
                case Severity.Info:
                    ConsoleText.WriteStatus(message);
                    break;
                case Severity.Verbose:
                case Severity.Verbose2:
                case Severity.Verbose3:
                    ConsoleText.WriteUsage(message);
                    break;
                case Severity.Warning:
                    ConsoleText.WriteWarning(message);
                    break;
                case Severity.Off:
                    return;
                default:
                    TrackTrace(message);
                    break;
            }
        }

        public void TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties = null)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));
        }

        public void Flush() { }
        public void Close() { }
    }
}
