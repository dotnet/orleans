
using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Support backwards compatability with old TraceManager for logger managament api
    /// </summary>
    [Obsolete("Replaced by LogManager.")]
    public class TraceLogger : LogManager
    {
    }
}
