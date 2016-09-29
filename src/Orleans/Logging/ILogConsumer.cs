using System;
using System.Net;

namespace Orleans.Runtime
{
    /// <summary>
    /// The ILogConsumer distinguishes between four categories of logs:
    /// <list type="table"><listheader><term>Value</term><description>Description</description></listheader>
    /// <item>
    /// <term>Runtime</term>
    /// <description>Logs that are written by the Orleans run-time itself.
    /// This category should not be used by application code.</description>
    /// </item>
    /// <item>
    /// <term>Grain</term>
    /// <description>Logs that are written by application grains.
    /// This category should be used by code that runs as Orleans grains in a silo.</description>
    /// </item>
    /// <item>
    /// <term>Application</term>
    /// <description>Logs that are written by the client application.
    /// This category should be used by client-side application code.</description>
    /// </item>
    /// <item>
    /// <term>Provider</term>
    /// <description>Logs that are written by providers.
    /// This category should be used by provider code.</description>
    /// </item>
    /// </list>
    /// </summary>
    public enum LoggerType
    {
        Runtime,
        Grain,
        Application,
        Provider
    }

    /// <summary>
    /// An interface used to consume log entries. 
    /// Instaces of a class implementing this should be added to <see cref="LogManager.LogConsumers"/> collection in order to retrieve events.
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
            Severity severity,
            LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIPEndPoint,
            Exception exception,
            int eventCode = 0
            );
    }

    /// <summary>
    /// An interface used to consume log entries, when a Flush function is also supported. 
    /// Instances of a class implementing this should be added to <see cref="LogManager.LogConsumers"/> collection in order to retrieve events.
    /// </summary>
    public interface IFlushableLogConsumer : ILogConsumer
    {
        /// <summary>Flush any pending log writes.</summary>
        void Flush();
    }

    /// <summary>
    /// An interface used to consume log entries, when a Close function is also supported. 
    /// Instances of a class implementing this should be added to <see cref="LogManager.LogConsumers"/> collection in order to retrieve events.
    /// </summary>
    public interface ICloseableLogConsumer : ILogConsumer
    {
        /// <summary>Close this log.</summary>
        void Close();
    }
}
