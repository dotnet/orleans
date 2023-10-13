namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestConsumer : IGrainWithIntegerKey
    {
        Task<int> PollProducer();
        Task Setup();
    }
}
