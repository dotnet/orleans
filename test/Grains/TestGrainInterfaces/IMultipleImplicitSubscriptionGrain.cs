namespace UnitTests.GrainInterfaces
{
    public interface IMultipleImplicitSubscriptionGrain : IGrainWithGuidKey
    {
        Task<Tuple<int, int>> GetCounters();
    }
}
