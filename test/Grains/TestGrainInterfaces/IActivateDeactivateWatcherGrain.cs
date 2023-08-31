namespace UnitTests.GrainInterfaces
{
    public interface IActivateDeactivateWatcherGrain : IGrainWithIntegerKey
    {
        Task<string[]> GetActivateCalls();
        Task<string[]> GetDeactivateCalls();

        Task Clear();

        Task RecordActivateCall(string activation);
        Task RecordDeactivateCall(string activation);
    }
}
