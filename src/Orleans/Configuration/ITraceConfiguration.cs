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
        /// The TraceFileName attribute specifies the name of a file that trace output should be written to.
        /// </summary>
        string TraceFileName { get; set; }
        /// <summary>
        /// The TraceFilePattern attribute specifies the pattern name of a file that trace output should be written to.
        /// </summary>
        string TraceFilePattern { get; set; }
    }
}
