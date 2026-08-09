namespace UnitTests.GrainInterfaces
{
    public interface IImplicitSubscriptionKeyTypeGrain
    {
        Task<int> GetValue(CancellationToken cancellationToken = default);
    }

    public interface IImplicitSubscriptionLongKeyGrain : IImplicitSubscriptionKeyTypeGrain, IGrainWithIntegerKey
    { }
}