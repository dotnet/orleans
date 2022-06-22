using Microsoft.Extensions.Logging;
using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Extension methods which preserves legacy orleans log methods style
    /// </summary>
    public static class OrleansLoggerExtension
    {
        /// <summary>
        /// Writes a log entry at the Information Level
        /// </summary>
        /// <param name="logger">Target logger.</param>
        /// <param name="format">Format string of the log message with named parameters
        /// <remarks>Not always suitable for <c>String.Format</c>. See Microsoft.Extensions.Logging MessageTemplate section for more information. Suggest to use their pattern over this extension method</remarks>
        /// </param>
        /// <param name="args">Any arguments to the format string.</param>
        public static void Info(this ILogger logger, string format, params object[] args)
        {
            logger.LogInformation(format, args);
        }
    }
}