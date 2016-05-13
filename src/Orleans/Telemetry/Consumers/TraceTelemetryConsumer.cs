﻿using System.Collections.Generic;
using System.Diagnostics;

namespace Orleans.Runtime
{
    public class TraceTelemetryConsumer : ITraceTelemetryConsumer
    {
        public void TrackTrace(string message)
        {
            Trace.TraceInformation(message);
        }

        public void TrackTrace(string message, IDictionary<string, string> properties)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties));            
        }

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

        public void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            TrackTrace(TraceParserUtils.PrintProperties(message, properties), severity);
        }

        public void Flush()
        {
            Trace.Flush();
        }

        public void Close()
        {
            Trace.Close();
        }
    }
}
