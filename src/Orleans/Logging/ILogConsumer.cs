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
using System.Net;

namespace Orleans.Runtime
{
    /// <summary>
    /// An interface used to consume log entries. 
    /// Instaces of a class implementing this should be added to <see cref="TraceLogger.LogConsumers"/> collection in order to retrieve events.
    /// </summary>
    public interface ILogConsumer
    {
        /// <summary>
        /// The method to call during logging.
        /// This method should be very fast, since it is called synchronously during Orleans logging.
        /// </summary>
        /// <param name="severity">The severity of the message being traced.</param>
        /// <param name="loggerType">The type of logger the message is being traced through.</param>
        /// <param name="caller">The name of the logger tracing the message.</param>
        /// <param name="myIPEndPoint">The <see cref="IPEndPoint"/> of the Orleans client/server if known. May be null.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="exception">The exception to log. May be null.</param>
        /// <param name="eventCode">Numeric event code for this log entry. May be zero, meaning 'Unspecified'. 
        /// In general, all log entries at severity=Error or greater should specify an explicit error code value.</param>
        void Log(
            Logger.Severity severity,
            TraceLogger.LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int eventCode = 0
            );
    }

    /// <summary>
    /// An interface used to consume log entries, when a Flush function is also supported. 
    /// Instances of a class implementing this should be added to <see cref="TraceLogger.LogConsumers"/> collection in order to retrieve events.
    /// </summary>
    public interface IFlushableLogConsumer : ILogConsumer
    {
        /// <summary>Flush any pending log writes.</summary>
        void Flush();
    }

    /// <summary>
    /// An interface used to consume log entries, when a Close function is also supported. 
    /// Instances of a class implementing this should be added to <see cref="TraceLogger.LogConsumers"/> collection in order to retrieve events.
    /// </summary>
    public interface ICloseableLogConsumer : ILogConsumer
    {
        /// <summary>Close this log.</summary>
        void Close();
    }
}
