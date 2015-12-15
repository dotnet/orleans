using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers;


namespace Orleans.Runtime
{
    internal class SiloControl : SystemTarget, ISiloControl
    {
        private readonly Silo silo;
        private static readonly TraceLogger logger = TraceLogger.GetLogger("SiloControl", TraceLogger.LoggerType.Runtime);

        public SiloControl(Silo silo)
            : base(Constants.SiloControlId, silo.SiloAddress)
        {
            this.silo = silo;
        }

        #region Implementation of ISiloControl

        public Task Ping(string message)
        {
            logger.Info("Ping");
            return TaskDone.Done;
        }

        public Task SetSystemLogLevel(int traceLevel)
        {
            var newTraceLevel = (Severity)traceLevel;
            logger.Info("SetSystemLogLevel={0}", newTraceLevel);
            TraceLogger.SetRuntimeLogLevel(newTraceLevel);
            silo.LocalConfig.DefaultTraceLevel = newTraceLevel;
            return TaskDone.Done;
        }

        public Task SetAppLogLevel(int traceLevel)
        {
            var newTraceLevel = (Severity)traceLevel;
            logger.Info("SetAppLogLevel={0}", newTraceLevel);
            TraceLogger.SetAppLogLevel(newTraceLevel);
            return TaskDone.Done;
        }

        public Task SetLogLevel(string logName, int traceLevel)
        {
            var newTraceLevel = (Severity)traceLevel;
            logger.Info("SetLogLevel[{0}]={1}", logName, newTraceLevel);
            TraceLogger log = TraceLogger.FindLogger(logName);
            
            if (log == null) throw new ArgumentException(string.Format("Logger {0} not found", logName));
            
            log.SetSeverityLevel(newTraceLevel);
            return TaskDone.Done;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect")]
        public Task ForceGarbageCollection()
        {
            logger.Info("ForceGarbageCollection");
            GC.Collect();
            return TaskDone.Done;
        }

        public Task ForceActivationCollection(TimeSpan ageLimit)
        {
            logger.Info("ForceActivationCollection");
            return InsideRuntimeClient.Current.Catalog.CollectActivations(ageLimit);
        }

        public Task ForceRuntimeStatisticsCollection()
        {
            if (logger.IsVerbose) logger.Verbose("ForceRuntimeStatisticsCollection");
            return DeploymentLoadPublisher.Instance.RefreshStatistics();
        }

        public Task<SiloRuntimeStatistics> GetRuntimeStatistics()
        {
            if (logger.IsVerbose) logger.Verbose("GetRuntimeStatistics");
            return Task.FromResult(new SiloRuntimeStatistics(silo.Metrics, DateTime.UtcNow));
        }

        public Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics()
        {
            logger.Info("GetGrainStatistics");
            return Task.FromResult( InsideRuntimeClient.Current.Catalog.GetGrainStatistics());
        }

        public Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics()
        {
            logger.Info("GetSimpleGrainStatistics");
            return Task.FromResult( InsideRuntimeClient.Current.Catalog.GetSimpleGrainStatistics().Select(p =>
                new SimpleGrainStatistic { SiloAddress = silo.SiloAddress, GrainType = p.Key, ActivationCount = (int)p.Value }).ToArray());
        }

        public Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId)
        {
            logger.Info("DetailedGrainReport for grain id {0}", grainId);
            return Task.FromResult( InsideRuntimeClient.Current.Catalog.GetDetailedGrainReport(grainId));
        }

        public Task UpdateConfiguration(string configuration)
        {
            logger.Info("UpdateConfiguration with {0}", configuration);
            silo.OrleansConfig.Update(configuration);
            logger.Info(ErrorCode.Runtime_Error_100318, "UpdateConfiguration - new config is now {0}", silo.OrleansConfig.ToString(silo.Name));
            return TaskDone.Done;
        }

        public Task<int> GetActivationCount()
        {
            return Task.FromResult(InsideRuntimeClient.Current.Catalog.ActivationCount);
        }

        public Task<object> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg)
        {
            IProvider provider = silo.AllSiloProviders.FirstOrDefault(pr => pr.GetType().FullName.Equals(providerTypeFullName) && pr.Name.Equals(providerName));
            if (provider == null)
            {
                throw new ArgumentException("Could not find provider for type " + providerTypeFullName + " and name " + providerName);
            }

            var controllable = provider as IControllable;
            if (controllable == null)
            {
                throw new ArgumentException("The found provider of type " + providerTypeFullName + " and name " + providerName + " is not controllable.");
            }
            return controllable.ExecuteCommand(command, arg);
        }

        #endregion
    }
}
