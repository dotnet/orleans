
using System;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Core;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Interface to be implemented for a log consistency provider.
    /// </summary>
    public interface ILogConsistencyProvider : IProvider, ILogViewAdaptorFactory
    {
        /// <summary>Gets the TraceLogger used by this log-consistency provider.</summary>
        Logger Log { get; }

    }
}
