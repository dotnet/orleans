namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestProducer : IGrainObserver
    {
        Task<int> Poll();
    }
}
