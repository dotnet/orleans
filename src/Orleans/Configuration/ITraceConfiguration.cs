using System;
using System.Collections.Generic;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// The TracingConfiguration type contains various tracing-related configuration parameters.
    /// For production use, the default value of these parameters should be fine.
    /// </summary>
    public interface ITraceConfiguration
    {
        /// <summary>
        /// The DefaultTraceLevel attribute specifies the default tracing level for all Orleans loggers, unless overridden by
        /// a specific TraceLevelOverride element.
        /// The default level is Info if this attribute does not appear.
        /// </summary>
        Severity DefaultTraceLevel { get; set; }
        /// <summary>
        /// The TraceFileName attribute specifies the name of a file that trace output should be written to.
        /// </summary>
        string TraceFileName { get; set; }
        /// <summary>
        /// The TraceFilePattern attribute specifies the pattern name of a file that trace output should be written to.
        /// </summary>
        string TraceFilePattern { get; set; }
        /// <summary>
        /// The TraceLevelOverride element provides a mechanism to allow the tracing level to be set differently for different
        /// parts of the Orleans system.
        /// The tracing level for a logger is set based on a prefix match on the logger's name.
        /// TraceLevelOverrides are applied in length order; that is, the override with the longest matching
        /// LogPrefix takes precedence and specifies the tracing level for all matching loggers.
        /// </summary>
        IList<Tuple<string, Severity>> TraceLevelOverrides { get; }
        /// <summary>
        /// The TraceToConsole attribute specifies whether trace output should be written to the console.
        /// The default is write trace data to the console if available.
        /// </summary>
        bool TraceToConsole { get; set; }
        /// <summary>
        /// The LargeMessageWarningThreshold attribute specifies when to generate a warning trace message for large messages.
        /// </summary>
        int LargeMessageWarningThreshold { get; set; }
        /// <summary>
        /// The PropagateActivityId attribute specifies whether the value of Tracing.CorrelationManager.ActivityId should be propagated into grain calls, to support E2E tracing.
        /// The default is not to propagate ActivityId.
        /// </summary>
        bool PropagateActivityId { get; set; }
        /// <summary>
        /// The BulkMessageLimit attribute specifies how to bulk (aggregate) trace messages with identical erro code.
        /// </summary>
        int BulkMessageLimit { get; set; }
    }
}
