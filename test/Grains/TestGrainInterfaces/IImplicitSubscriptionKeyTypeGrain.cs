namespace UnitTests.GrainInterfaces
{
    public interface IImplicitSubscriptionKeyTypeGrain
    {
        Task<int> GetValue();
    }

    public interface IImplicitSubscriptionLongKeyGrain : IImplicitSubscriptionKeyTypeGrain, IGrainWithIntegerKey
    { }
}