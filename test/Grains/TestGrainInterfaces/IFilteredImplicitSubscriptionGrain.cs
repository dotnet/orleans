namespace UnitTests.GrainInterfaces
{
    public interface IFilteredImplicitSubscriptionGrain : IGrainWithGuidKey
    {
        Task<int> GetCounter(string streamNamespace);
    }
}