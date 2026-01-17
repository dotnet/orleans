using Orleans.Concurrency;

namespace UnitTests.GrainInterfaces
{
    public interface IImplicitSubscriptionCounterGrain : IGrainWithGuidKey
    {
        Task<int> GetEventCounter();

        Task<int> GetErrorCounter();

        /// <summary>
        /// Waits until the event counter reaches or exceeds the expected count.
        /// </summary>
        /// <param name="expectedCount">The minimum number of events to wait for.</param>
        /// <param name="timeout">Maximum time to wait.</param>
        /// <returns>The current event count when the condition is met.</returns>
        [AlwaysInterleave]
        Task<int> WaitForEventCount(int expectedCount, TimeSpan timeout);

        Task Deactivate();

        Task DeactivateOnEvent(bool deactivate);
    }

    public interface IFastImplicitSubscriptionCounterGrain : IImplicitSubscriptionCounterGrain
    { }

    public interface ISlowImplicitSubscriptionCounterGrain : IImplicitSubscriptionCounterGrain
    { }
}