using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Placement;

namespace LoadTestGrainInterfaces
{
    public enum BenchmarkGrainType
    {
        RandomNonReentrant,
        RandomReentrant,
        LocalNonReentrant,        
        LocalReentrant,
        GraphPartitionReentrant
    }

    public interface IBenchmarkLoadGrain : IGrain
    {
        Task Initialize();

        Task<byte[]> Echo(byte[] data);

        Task<string> GetRuntimeInstanceId();

        Task Ping(byte[] data);

        Task PingImmutable(Immutable<byte[]> data);

        Task PingImmutableWithDelay(Immutable<byte[]> data, TimeSpan delay);

        Task PingMutableArray_TwoHop(byte[] data, Guid nextGrain, BenchmarkGrainType type);

        Task PingImmutableArray_TwoHop(Immutable<byte[]> data, Guid nextGrain, BenchmarkGrainType type);

        Task PingMutableDictionary_TwoHop(Dictionary<int, string> data, Guid nextGrain, BenchmarkGrainType type);

        Task PingImmutableDictionary_TwoHop(Immutable<Dictionary<int, string>> data, Guid nextGrain, BenchmarkGrainType type);

        Task RandomWalk(Immutable<byte[]> data, Guid[] walk, int position, BenchmarkGrainType type);

        Task PingSessionToPlayer(Immutable<byte[]> data, Guid[] players, bool isSession, BenchmarkGrainType type);
    }

    public interface IRandomNonReentrantBenchmarkLoadGrain : IBenchmarkLoadGrain
    {
    }

    public interface IRandomReentrantBenchmarkLoadGrain : IBenchmarkLoadGrain
    {
    }

    public interface ILocalNonReentrantBenchmarkLoadGrain : IBenchmarkLoadGrain
    {
    }

    public interface ILocalReentrantBenchmarkLoadGrain : IBenchmarkLoadGrain
    {
    }

    /*
    [GraphPartitionPlacementAttribute]
    public interface IReentrantGraphPartitionBenchmarkLoadGrain : IReentrantBenchmarkLoadGrain
    {
    }*/
}
