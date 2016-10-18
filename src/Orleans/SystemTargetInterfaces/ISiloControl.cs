using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans
{
    internal interface ISiloControl : ISystemTarget
    {
        Task Ping(string message);

        Task SetSystemLogLevel(int traceLevel);
        Task SetAppLogLevel(int traceLevel);
        Task SetLogLevel(string logName, int traceLevel);

        Task ForceGarbageCollection();
        Task ForceActivationCollection(TimeSpan ageLimit);
        Task ForceRuntimeStatisticsCollection();

        Task<SiloRuntimeStatistics> GetRuntimeStatistics();
        Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics();
        Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[] types = null);
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics();
        Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId);

        Task UpdateConfiguration(string configuration);

        Task UpdateStreamProviders(IDictionary<string, ProviderCategoryConfiguration> streamProviderConfigurations);
  
        Task<int> GetActivationCount();

        Task<object> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg);
        Task<string[]> GetGrainTypeList();
    }
}
