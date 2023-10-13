namespace UnitTests.GrainInterfaces
{
    public interface ICatalogTestGrain : IGrainWithIntegerKey
    {
        Task Initialize();
        Task BlastCallNewGrains(int nGrains, long startingKey, int nCallsToEach);
    }
}
