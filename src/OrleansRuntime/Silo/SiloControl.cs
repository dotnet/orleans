using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;


namespace Orleans.Runtime
{
    internal class SiloControl : SystemTarget, ISiloControl
    {
        private static readonly Logger logger = LogManager.GetLogger("SiloControl", LoggerType.Runtime);
        private readonly Silo silo;
        private readonly DeploymentLoadPublisher deploymentLoadPublisher;
        private readonly Catalog catalog;
        private readonly GrainTypeManager grainTypeManager;
        private readonly ISiloPerformanceMetrics siloMetrics;
        private readonly CachedVersionSelectorManager cachedVersionSelectorManager;
        private readonly CompatibilityDirectorManager compatibilityDirectorManager;
        private readonly VersionSelectorManager selectorManager;

        public SiloControl(
            Silo silo,
            DeploymentLoadPublisher deploymentLoadPublisher,
            Catalog catalog,
            GrainTypeManager grainTypeManager,
            ISiloPerformanceMetrics siloMetrics, 
            CachedVersionSelectorManager cachedVersionSelectorManager, 
            CompatibilityDirectorManager compatibilityDirectorManager,
            VersionSelectorManager selectorManager)
            : base(Constants.SiloControlId, silo.SiloAddress)
        {
            this.silo = silo;
            this.deploymentLoadPublisher = deploymentLoadPublisher;
            this.catalog = catalog;
            this.grainTypeManager = grainTypeManager;
            this.siloMetrics = siloMetrics;
            this.cachedVersionSelectorManager = cachedVersionSelectorManager;
            this.compatibilityDirectorManager = compatibilityDirectorManager;
            this.selectorManager = selectorManager;
        }

        #region Implementation of ISiloControl

        public Task Ping(string message)
        {
            logger.Info("Ping");
            return Task.CompletedTask;
        }

        public Task SetSystemLogLevel(int traceLevel)
        {
            var newTraceLevel = (Severity)traceLevel;
            logger.Info("SetSystemLogLevel={0}", newTraceLevel);
            LogManager.SetRuntimeLogLevel(newTraceLevel);
            silo.LocalConfig.DefaultTraceLevel = newTraceLevel;
            return Task.CompletedTask;
        }

        public Task SetAppLogLevel(int traceLevel)
        {
            var newTraceLevel = (Severity)traceLevel;
            logger.Info("SetAppLogLevel={0}", newTraceLevel);
            LogManager.SetAppLogLevel(newTraceLevel);
            return Task.CompletedTask;
        }

        public Task SetLogLevel(string logName, int traceLevel)
        {
            var newTraceLevel = (Severity)traceLevel;
            logger.Info("SetLogLevel[{0}]={1}", logName, newTraceLevel);
            LoggerImpl log = LogManager.FindLogger(logName);
            
            if (log == null) throw new ArgumentException(string.Format("Logger {0} not found", logName));
            
            log.SetSeverityLevel(newTraceLevel);
            return Task.CompletedTask;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId = "System.GC.Collect")]
        public Task ForceGarbageCollection()
        {
            logger.Info("ForceGarbageCollection");
            GC.Collect();
            return Task.CompletedTask;
        }

        public Task ForceActivationCollection(TimeSpan ageLimit)
        {
            logger.Info("ForceActivationCollection");
            return this.catalog.CollectActivations(ageLimit);
        }

        public Task ForceRuntimeStatisticsCollection()
        {
            if (logger.IsVerbose) logger.Verbose("ForceRuntimeStatisticsCollection");
            return this.deploymentLoadPublisher.RefreshStatistics();
        }

        public Task<SiloRuntimeStatistics> GetRuntimeStatistics()
        {
            if (logger.IsVerbose) logger.Verbose("GetRuntimeStatistics");
            return Task.FromResult(new SiloRuntimeStatistics(this.siloMetrics, DateTime.UtcNow));
        }

        public Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics()
        {
            logger.Info("GetGrainStatistics");
            return Task.FromResult(this.catalog.GetGrainStatistics());
        }

        public Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[] types=null)
        {
            if (logger.IsVerbose) logger.Verbose("GetDetailedGrainStatistics");
            return Task.FromResult(this.catalog.GetDetailedGrainStatistics(types));
        }

        public Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics()
        {
            logger.Info("GetSimpleGrainStatistics");
            return Task.FromResult( this.catalog.GetSimpleGrainStatistics().Select(p =>
                new SimpleGrainStatistic { SiloAddress = silo.SiloAddress, GrainType = p.Key, ActivationCount = (int)p.Value }).ToArray());
        }

        public Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId)
        {
            logger.Info("DetailedGrainReport for grain id {0}", grainId);
            return Task.FromResult( this.catalog.GetDetailedGrainReport(grainId));
        }

        public Task UpdateConfiguration(string configuration)
        {
            logger.Info("UpdateConfiguration with {0}", configuration);
            silo.OrleansConfig.Update(configuration);
            logger.Info(ErrorCode.Runtime_Error_100318, "UpdateConfiguration - new config is now {0}", silo.OrleansConfig.ToString(silo.Name));
            return Task.CompletedTask;
        }

        public Task UpdateStreamProviders(IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations)
        {
            return silo.UpdateStreamProviders(streamProviderConfigurations);
        }

        public Task<int> GetActivationCount()
        {
            return Task.FromResult(this.catalog.ActivationCount);
        }

        public Task<object> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg)
        {
            IReadOnlyCollection<IProvider> allProviders = silo.AllSiloProviders;
            IProvider provider = allProviders.FirstOrDefault(pr => pr.GetType().FullName.Equals(providerTypeFullName) && pr.Name.Equals(providerName));
            if (provider == null)
            {
                string allProvidersList = Utils.EnumerableToString(
                    allProviders.Select(p => string.Format(
                        "[Name = {0} Type = {1} Location = {2}]",
                        p.Name, p.GetType().FullName, p.GetType().GetTypeInfo().Assembly.Location)));
                string error = string.Format(
                    "Could not find provider for type {0} and name {1} \n"
                    + " Providers currently loaded in silo are: {2}", 
                    providerTypeFullName, providerName, allProvidersList);
                logger.Error(ErrorCode.Provider_ProviderNotFound, error);
                throw new ArgumentException(error);
            }

            IControllable controllable = provider as IControllable;
            if (controllable == null)
            {
                string error = string.Format(
                    "The found provider of type {0} and name {1} is not controllable.", 
                    providerTypeFullName, providerName);
                logger.Error(ErrorCode.Provider_ProviderNotControllable, error);
                throw new ArgumentException(error);
            }
            return controllable.ExecuteCommand(command, arg);
        }

        public Task<string[]> GetGrainTypeList()
        {
            return Task.FromResult(this.grainTypeManager.GetGrainTypeList());
        }

        public Task SetCompatibilityStrategy(CompatibilityStrategy strategy)
        {
            this.compatibilityDirectorManager.SetStrategy(strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetSelectorStrategy(VersionSelectorStrategy strategy)
        {
            this.selectorManager.SetSelector(strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetCompatibilityStrategy(int interfaceId, CompatibilityStrategy strategy)
        {
            this.compatibilityDirectorManager.SetStrategy(interfaceId, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        public Task SetSelectorStrategy(int interfaceId, VersionSelectorStrategy strategy)
        {
            this.selectorManager.SetSelector(interfaceId, strategy);
            this.cachedVersionSelectorManager.ResetCache();
            return Task.CompletedTask;
        }

        #endregion
    }
}
