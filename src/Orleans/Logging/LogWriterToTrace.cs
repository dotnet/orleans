/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// The Log Writer class is a convenient wrapper around the .Net Trace class.
    /// </summary>
    public class LogWriterToTrace : LogWriterBase, IFlushableLogConsumer
    {
        private static readonly TraceSource _orleansTraceSource = new TraceSource("Orleans", SourceLevels.Error);
        private static readonly TraceSource _orleansRuntimeTraceSource = new TraceSource("Orleans.Runtime", SourceLevels.Error);
        private static readonly TraceSource _orleansGrainTraceSource = new TraceSource("Orleans.Grain", SourceLevels.Error);
        private static readonly TraceSource _orleansApplicationTraceSource = new TraceSource("Orleans.Application", SourceLevels.Error);
        private static readonly TraceSource _orleansProviderTraceSource = new TraceSource("Orleans.Provider", SourceLevels.Error);

        protected override string FormatLogMessage(
            DateTime timestamp,
            Logger.Severity severity,
            TraceLogger.LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int errorCode)
        {
            var msg = base.FormatLogMessage(timestamp, severity, loggerType, caller, message, myIPEndPoint, exception, errorCode);

            TraceEventType teType;
            switch (severity)
            {
                case Logger.Severity.Off:
                    teType = TraceEventType.Critical;
                    break;
                case Logger.Severity.Error:
                    teType = TraceEventType.Error;
                    break;
                case Logger.Severity.Warning:
                    teType = TraceEventType.Warning;
                    break;
                case Logger.Severity.Info:
                    teType = TraceEventType.Information;
                    break;
                case Logger.Severity.Verbose:
                    teType = TraceEventType.Verbose;
                    break;
                case Logger.Severity.Verbose2:
                    teType = TraceEventType.Verbose;
                    break;
                case Logger.Severity.Verbose3:
                    teType = TraceEventType.Verbose;
                    break;
                default:
                    teType = TraceEventType.Error;
                    break;
            }

            TraceSource tsRef;
            switch (loggerType)
            {
                case TraceLogger.LoggerType.Runtime:
                    tsRef = _orleansRuntimeTraceSource;
                    break;
                case TraceLogger.LoggerType.Grain:
                    tsRef = _orleansGrainTraceSource;
                    break;
                case TraceLogger.LoggerType.Application:
                    tsRef = _orleansApplicationTraceSource;
                    break;
                case TraceLogger.LoggerType.Provider:
                    tsRef = _orleansProviderTraceSource;
                    break;
                default:
                    tsRef = _orleansRuntimeTraceSource;
                    break;
            }

            if (exception == null)
            {
                //tsRef.TraceEvent(teType, errorCode, msg);
                tsRef.TraceEvent(teType, errorCode, "{0} from {1} at {2}", message, caller, myIPEndPoint);
            }
            else
            {
                //tsRef.TraceData(teType, errorCode, exception, myIPEndPoint);
                tsRef.TraceData(teType, errorCode, exception);
            }

            return msg;
        }

        /// <summary>Write the log message for this log.</summary>
        protected override void WriteLogMessage(string msg, Logger.Severity severity)
        {
            //because we want the greater detail of the trace, not just a formatted string
            // we shall cheat and use the formatting call to Trace.Write(xxxx), this is in lue of altering the abstract class

            //so, dont bother with this
            //switch (severity)
            //{
            //    case Logger.Severity.Off:
            //        break;
            //    case Logger.Severity.Error:
            //        Trace.TraceError(msg);
            //        break;
            //    case Logger.Severity.Warning:
            //        Trace.TraceWarning(msg);
            //        break;
            //    case Logger.Severity.Info:
            //        Trace.TraceInformation(msg);
            //        break;
            //    case Logger.Severity.Verbose:
            //    case Logger.Severity.Verbose2:
            //    case Logger.Severity.Verbose3:
            //        Trace.WriteLine(msg);
            //        break;
            //}
            Flush();
        }

        /// <summary>Flush any pending output for this log.</summary>
        public void Flush()
        {
            Trace.Flush();
        }
    }
}