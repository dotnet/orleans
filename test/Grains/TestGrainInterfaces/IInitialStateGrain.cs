namespace UnitTests.GrainInterfaces
{
    public interface IInitialStateGrain : IGrainWithIntegerKey
    {
        Task<List<string>> GetNames();
        Task AddName(string name);
    }
}
