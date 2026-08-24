namespace UnitTests.GrainInterfaces
{
    public interface IImplicitSubscriptionCounterGrain : IGrainWithGuidKey
    {
        Task<int> GetEventCounter(CancellationToken cancellationToken = default);

        Task<int> GetErrorCounter();

        Task Deactivate();

        Task DeactivateOnEvent(bool deactivate);

        Task ReplaceObserverOnNextEvent();

        Task RewindToFirstToken();
    }

    public interface IFastImplicitSubscriptionCounterGrain : IImplicitSubscriptionCounterGrain
    { }

    public interface ISlowImplicitSubscriptionCounterGrain : IImplicitSubscriptionCounterGrain
    { }
}