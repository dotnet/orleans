using Orleans.CodeGeneration;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Orleans.Runtime.TestHooks
{
    internal interface ITestHooksSystemTarget : ISystemTarget
    {
        Task<SiloAddress> GetPrimaryTargetSilo(uint key);
        Task<string> GetConsistentRingProviderString();
        Task<bool> HasStatisticsProvider();
        Task<Guid> GetServiceId();
        Task<IEnumerable<string>> GetStorageProviderNames();
        Task<IEnumerable<string>> GetStreamProviderNames();
        Task<IEnumerable<string>> GetAllSiloProviderNames();
        Task SuppressFastKillInHandleProcessExit();
        Task<IDictionary<GrainId, IGrainInfo>> GetDirectoryForTypeNamesContaining(string expr);
        Task BlockSiloCommunication(IPEndPoint destination, double lost_percentage);
        Task UnblockSiloCommunication();
        Task<bool> ShouldDrop(Message msg);
        Task<int> UnregisterGrainForTesting(GrainId grain);
        Task SetDirectoryLazyDeregistrationDelay(TimeSpan timeSpan);
        Task SetMaxForwardCount(int val);
        Task DecideToCollectActivation(GrainId grainId);
        Task RegisterTestHooksObserver(ITestHooksObserver observer);
    }

    internal interface ITestHooksObserver : IGrainObserver
    {
        void OnCollectActivation(GrainId grainId);
    }

}
