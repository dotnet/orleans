namespace UnitTests.GrainInterfaces
{
    public interface IActivityGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
    }

    /// <summary>
    /// Grain interface for testing IAsyncEnumerable activity tracing.
    /// </summary>
    public interface IAsyncEnumerableActivityGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// Gets multiple ActivityData items as an async enumerable.
        /// Each item captures the current Activity context at the time of yield.
        /// </summary>
        /// <param name="count">Number of items to yield.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of ActivityData items.</returns>
        IAsyncEnumerable<ActivityData> GetActivityDataStream(int count, CancellationToken cancellationToken = default);
    }

    [GenerateSerializer]
    public class ActivityData
    {
        [Id(0)]
        public string Id { get; set; }

        [Id(1)]
        public string TraceState { get; set; }

        [Id(2)]
        public List<KeyValuePair<string, string>> Baggage { get; set; }
    }
}
