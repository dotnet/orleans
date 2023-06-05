namespace DistributedTests.GrainInterfaces
{
    public interface IPingGrain : IGrainWithGuidKey
    {
        ValueTask Ping();
    }
}
