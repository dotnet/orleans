namespace UnitTests.GrainInterfaces
{
    public interface IStreamInterceptionGrain : IGrainWithGuidKey
    {
        Task<int> GetLastStreamValue();
    }
}