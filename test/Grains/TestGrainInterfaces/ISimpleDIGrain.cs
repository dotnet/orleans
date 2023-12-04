namespace UnitTests.GrainInterfaces
{
    public interface ISimpleDIGrain : IGrainWithIntegerKey
    {
        Task<long> GetLongValue();
        Task<string> GetStringValue();
        Task DoDeactivate();
    }

    public interface IDIGrainWithInjectedServices : ISimpleDIGrain
    {
        Task<int> GetGrainFactoryId();
        Task<string> GetInjectedSingletonServiceValue();
        Task<string> GetInjectedScopedServiceValue();
        Task AssertCanResolveSameServiceInstances();
    }
}
