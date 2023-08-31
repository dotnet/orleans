namespace UnitTests.GrainInterfaces
{
    public interface IFilteredImplicitSubscriptionWithExtensionGrain : IGrainWithGuidCompoundKey
    {
        Task<int> GetCounter();
    }
}