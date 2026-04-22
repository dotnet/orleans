using System;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing;
using Orleans.Serialization;

namespace Orleans.Runtime.LogConsistency
{
    /// <summary>
    /// Functionality for use by log view adaptors that run distributed protocols.
    /// This class allows access to these services to providers that cannot see runtime-internals.
    /// It also stores grain-specific information like the grain reference, and caches
    /// </summary>
    internal partial class ProtocolServices : ILogConsistencyProtocolServices
    {
        private readonly ILogger log;
        private readonly DeepCopier deepCopier;
        private readonly IGrainContext grainContext;   // links to the grain that owns this service object

        public ProtocolServices(
            IGrainContext grainContext,
            ILoggerFactory loggerFactory,
            DeepCopier deepCopier,
            ILocalSiloDetails siloDetails)
        {
            this.grainContext = grainContext;
            this.log = loggerFactory.CreateLogger<ProtocolServices>();
            this.deepCopier = deepCopier;
            this.MyClusterId = siloDetails.ClusterId;
        }

        public GrainId GrainId => grainContext.GrainId;

        public string MyClusterId { get; }

        public T DeepCopy<T>(T value) => this.deepCopier.Copy(value);

        public void ProtocolError(string msg, bool throwexception)
        {
            if (throwexception)
            {
                LogErrorProtocolFatal(log, grainContext.GrainId, msg);
            }
            else
            {
                LogErrorProtocol(log, grainContext.GrainId, msg);
            }

            if (!throwexception)
                return;

            throw new OrleansException(string.Format("{0} (grain={1}, cluster={2})", msg, grainContext.GrainId, this.MyClusterId));
        }

        public void CaughtException(string where, Exception e)
        {
            LogErrorCaughtException(log, e, grainContext.GrainId, where);
        }

        public void CaughtUserCodeException(string callback, string where, Exception e)
        {
            LogWarningCaughtUserCodeException(log, e, grainContext.GrainId, callback, where);
        }

        public void Log(LogLevel level, string format, params object[] args)
        {
            if (log != null && log.IsEnabled(level))
            {
                var msg = $"{grainContext.GrainId} {string.Format(format, args)}";
                log.Log(level, 0, msg, null, (m, exc) => $"{m}");
            }
        }
    }

}
