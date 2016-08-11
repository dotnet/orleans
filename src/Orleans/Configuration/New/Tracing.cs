using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Orleans.Runtime.Configuration.New
{
    /// <summary>
    /// The TracingConfiguration type contains various tracing-related configuration parameters.
    /// For production use, the default value of these parameters should be fine.
    /// </summary>
    public class Tracing
    {
        private string traceFilePattern;

        public Tracing()
        {
            DefaultTraceLevel = Severity.Info;
            TraceToConsole = true;
            TraceFilePattern = "{0}-{1}.log";
            LargeMessageWarningThreshold = Constants.LARGE_OBJECT_HEAP_THRESHOLD;
            PropagateActivityId = Constants.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
            BulkMessageLimit = Constants.DEFAULT_LOGGER_BULK_MESSAGE_LIMIT;
        }

        /// <summary>
        /// The DefaultTraceLevel attribute specifies the default tracing level for all Orleans loggers, unless overridden by
        /// a specific TraceLevelOverride element.
        /// The default level is Info if this attribute does not appear.
        /// </summary>
        public Severity DefaultTraceLevel { get; set; }
        /// <summary>
        /// The TraceFileName attribute specifies the name of a file that trace output should be written to.
        /// </summary>
        public  string TraceFileName { get; set; }
        /// <summary>
        /// The TraceFilePattern attribute specifies the pattern name of a file that trace output should be written to.
        /// </summary>
        public string TraceFilePattern
        { 
            get { return traceFilePattern; }
            set
            {
                traceFilePattern = value;
                //ConfigUtilities.SetTraceFileName(this, ClientName, this.creationTimestamp);
            }
        } 
        /// <summary>
        /// The TraceLevelOverride element provides a mechanism to allow the tracing level to be set differently for different
        /// parts of the Orleans system.
        /// The tracing level for a logger is set based on a prefix match on the logger's name.
        /// TraceLevelOverrides are applied in length order; that is, the override with the longest matching
        /// LogPrefix takes precedence and specifies the tracing level for all matching loggers.
        /// </summary>
        public  List<TraceLevelOverride> TraceLevelOverrides { get; set; } = new List<TraceLevelOverride>();
        /// <summary>
        /// The TraceToConsole attribute specifies whether trace output should be written to the console.
        /// The default is not to write trace data to the console.
        /// </summary>
        public  bool TraceToConsole { get; set; }
        /// <summary>
        /// The LargeMessageWarningThreshold attribute specifies when to generate a warning trace message for large messages.
        /// </summary>
        public  int LargeMessageWarningThreshold { get; set; }
        /// <summary>
        /// The PropagateActivityId attribute specifies whether the value of Tracing.CorrelationManager.ActivityId should be propagated into grain calls, to support E2E tracing.
        /// The default is not to propagate ActivityId.
        /// </summary>
        public  bool PropagateActivityId { get; set; }
        /// <summary>
        /// The BulkMessageLimit attribute specifies how to bulk (aggregate) trace messages with identical erro code.
        /// </summary>
        public  int BulkMessageLimit { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("   Tracing: ").AppendLine();
            sb.Append("     Default Trace Level: ").Append(DefaultTraceLevel).AppendLine();
            if (TraceLevelOverrides.Count > 0)
            {
                sb.Append("     TraceLevelOverrides:").AppendLine();
                foreach (var over in TraceLevelOverrides)
                {
                    sb.Append("         ").Append(over.LogPrefix).Append(" ==> ").Append(over.TraceLevel.ToString()).AppendLine();
                }
            }
            else
            {
                sb.Append("     TraceLevelOverrides: None").AppendLine();
            }
            sb.Append("     Trace to Console: ").Append(TraceToConsole).AppendLine();
            sb.Append("     Trace File Name: ").Append(string.IsNullOrWhiteSpace(TraceFileName) ? "" : Path.GetFullPath(TraceFileName)).AppendLine();
            sb.Append("     LargeMessageWarningThreshold: ").Append(LargeMessageWarningThreshold).AppendLine();
            sb.Append("     PropagateActivityId: ").Append(PropagateActivityId).AppendLine();
            sb.Append("     BulkMessageLimit: ").Append(BulkMessageLimit).AppendLine();
            return sb.ToString();
        }
    }
}