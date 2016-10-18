using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime.TestHooks
{
    internal interface ITestHooks
    {
        Task<SiloAddress> GetConsistentRingPrimaryTargetSilo(uint key);
        Task<string> GetConsistentRingProviderDiagnosticInfo();
        Task<bool> HasStatisticsProvider();
        Task<Guid> GetServiceId();
        Task<ICollection<string>> GetStorageProviderNames();
        Task<ICollection<string>> GetStreamProviderNames();
        Task<ICollection<string>> GetAllSiloProviderNames();
        Task<int> UnregisterGrainForTesting(GrainId grain);
        Task LatchIsOverloaded(bool overloaded, TimeSpan latchPeriod);
    }

    internal interface ITestHooksSystemTarget : ITestHooks, ISystemTarget
    {
    }
}
