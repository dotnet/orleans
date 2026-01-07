using System.Net;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IPlacementTestGrain : IGrainWithGuidKey
    {
        Task<IPEndPoint> GetEndpoint();
        Task<string> GetRuntimeInstanceId();
        Task<string> GetActivationId();
        Task StartLocalGrains(List<Guid> keys);
        Task<Guid> StartPreferLocalGrain(Guid key);
        Task<List<IPEndPoint>> SampleLocalGrainEndpoint(Guid key, int sampleSize);
        Task Nop();
        Task EnableOverloadDetection(bool enabled);
        Task LatchOverloaded();
        Task UnlatchOverloaded();
        Task LatchCpuUsage(float value);
        Task UnlatchCpuUsage();
        /// <summary>
        /// Latches CPU usage on this silo without propagating statistics to the cluster.
        /// Use <see cref="RefreshOverloadDetectorAndPropagateStatistics"/> after latching all silos.
        /// </summary>
        Task LatchCpuUsageOnly(float value);
        /// <summary>
        /// Latches overloaded status on this silo without propagating statistics to the cluster.
        /// Use <see cref="RefreshOverloadDetectorAndPropagateStatistics"/> after latching all silos.
        /// </summary>
        Task LatchOverloadedOnly();
        /// <summary>
        /// Refreshes this silo's OverloadDetector cache and propagates statistics to all silos.
        /// Call this after latching CPU/overloaded on all silos to ensure deterministic behavior.
        /// </summary>
        Task RefreshOverloadDetectorAndPropagateStatistics();
        /// <summary>
        /// Atomically latches CPU usage and refreshes OverloadDetector, then propagates statistics.
        /// This avoids race conditions where the OverloadDetector auto-refreshes between latch and explicit refresh.
        /// </summary>
        Task LatchCpuUsageAndPropagate(float value);
        /// <summary>
        /// Atomically latches overloaded status and refreshes OverloadDetector, then propagates statistics.
        /// This avoids race conditions where the OverloadDetector auto-refreshes between latch and explicit refresh.
        /// </summary>
        Task LatchOverloadedAndPropagate();
        Task<SiloAddress> GetLocation();
    }

    public interface IActivationCountBasedPlacementTestGrain : IPlacementTestGrain
    { }

    public interface IRandomPlacementTestGrain : IPlacementTestGrain
    { }

    public interface IPreferLocalPlacementTestGrain : IPlacementTestGrain
    { }

    public interface IStatelessWorkerPlacementTestGrain : IPlacementTestGrain
    {
        ValueTask<int> GetWorkerLimit();
    }
    
    public interface IOtherStatelessWorkerPlacementTestGrain : IStatelessWorkerPlacementTestGrain
    {
    }

    internal interface IDefaultPlacementTestGrain
    {
        bool IsDefaultPlacementRandom();
    }

    //----------------------------------------------------------//
    // Interfaces for LocalContent grain case, when grain is activated on every silo by bootstrap provider.

    public interface ILocalContentGrain : IGrainWithGuidKey
    {
        Task Init();                            // a dummy call to just activate this grain.
        Task<object> GetContent();
    }

    public interface ITestContentGrain : IGrainWithIntegerKey
    {
        Task<string> GetRuntimeInstanceId();    // just for test
        Task<object> FetchContentFromLocalGrain();
    }

}
