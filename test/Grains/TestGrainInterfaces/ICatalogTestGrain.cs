namespace UnitTests.GrainInterfaces
{
    public interface ICatalogTestGrain : IGrainWithIntegerKey
    {
        Task Initialize();
        Task<string> GetActivationId();
        Task<string[]> GetActivationIds(int nGrains, long startingKey);
    }
}
