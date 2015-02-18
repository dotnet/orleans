using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Placement;


namespace UnitTestGrainInterfaces
{
    internal interface IPlacementTestGrain : IGrain
    {
        Task<IPEndPoint> GetEndpoint();
        Task<string> GetRuntimeInstanceId();
        Task<ActivationId> GetActivationId();
        Task StartLocalGrains(List<long> keys);
        Task<long> StartPreferLocalGrain(long key);
        Task<List<IPEndPoint>> SampleLocalGrainEndpoint(long key, int sampleSize);
        Task Nop();
        Task LatchOverloaded();
        Task UnlatchOverloaded();
        Task LatchCpuUsage(float value);
        Task UnlatchCpuUsage();
        Task<SiloAddress> GetLocation();
    }

    internal interface IActivationCountBasedPlacementTestGrain : IPlacementTestGrain
    { }

    internal interface IRandomPlacementTestGrain : IPlacementTestGrain
    { }

    internal interface IPreferLocalPlacementTestGrain : IPlacementTestGrain
    { }

    internal interface ILocalPlacementTestGrain : IPlacementTestGrain
    { }

    //----------------------------------------------------------//
    // Interfaces for LocalContent grain case, when grain is activated on every silo by bootstrap provider.

    public interface ILocalContentGrain : IGrain
    {
        Task Init();                            // a dummy call to just activate this grain.
        Task<object> GetContent();
    }

    public interface ITestContentGrain : IGrain
    {
        Task<string> GetRuntimeInstanceId();    // just for test
        Task<object> FetchContentFromLocalGrain();
    }

}
