using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    internal interface ISiloControl : ISystemTarget, IVersionManager
    {
        Task Ping(string message);

        Task ForceGarbageCollection();
        Task ForceActivationCollection(TimeSpan ageLimit);
        Task ForceRuntimeStatisticsCollection();

        Task<SiloRuntimeStatistics> GetRuntimeStatistics();
        Task<List<Tuple<GrainId, string, int>>> GetGrainStatistics();
        Task<List<DetailedGrainStatistic>> GetDetailedGrainStatistics(string[] types = null);
        Task<SimpleGrainStatistic[]> GetSimpleGrainStatistics();
        Task<DetailedGrainReport> GetDetailedGrainReport(GrainId grainId);

        Task<int> GetActivationCount();

        Task<object> SendControlCommandToProvider(string providerTypeFullName, string providerName, int command, object arg);
        Task<List<GrainId>> GetActiveGrains(GrainType grainType);
    }
}
