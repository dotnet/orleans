using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    public interface IReentrantStressTestGrain : IGrainWithIntegerKey
    {
        Task<byte[]> Echo(byte[] data);

        Task<string> GetRuntimeInstanceId();

        Task Ping(byte[] data);

        Task PingWithDelay(byte[] data, TimeSpan delay);

        Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote);

        Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote);

        Task InterleavingConsistencyTest(int numItems);
    }

    public interface IReentrantLocalStressTestGrain : IGrainWithIntegerKey
    {
        Task<byte[]> Echo(byte[] data);

        Task<string> GetRuntimeInstanceId();

        Task Ping(byte[] data);

        Task PingWithDelay(byte[] data, TimeSpan delay);

        Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote);

        Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote);

        Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain, bool nextGrainIsRemote);
    }
}
