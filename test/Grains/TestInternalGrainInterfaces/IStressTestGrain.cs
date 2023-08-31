using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    internal interface IStressTestGrain : IGrainWithIntegerKey
    {
        Task<string> GetLabel();

        Task SetLabel(string label);

        Task PingOthers(long[] others);

        Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> LookUpMany(SiloAddress destination, List<Tuple<GrainId, int>> grainAndETagList, int retries = 0);

        Task Send(byte[] data);

        Task<byte[]> Echo(byte[] data);

        Task Ping(byte[] data);

        Task PingWithDelay(byte[] data, TimeSpan delay);

        Task<IStressTestGrain> GetGrainReference();

        Task DeactivateSelf();
    }
}
