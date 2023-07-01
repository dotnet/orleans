namespace TestGrainInterfaces
{

    /// <summary>
    /// A grain that maintains a number of counters, indexed by a string key
    /// </summary>
    public interface ICountersGrain : Orleans.IGrainWithIntegerKey
    {
        /// <summary> Updates the counter for the given key by the given amount </summary>
        Task Add(string key, int amount, bool wait_for_confirmation);

        /// <summary> Resets all counters to zero </summary>
        Task Reset(bool wait_for_confirmation);

        /// <summary> Retrieves the tentative value of the counter for the given key </summary>
        Task<int> GetTentativeCount(string key);

        /// <summary> Retrieves the tentative value of all counters </summary>
        Task<IReadOnlyDictionary<string, int>> GetTentativeState();

        /// <summary> Retrieves the confirmed value of all counters </summary>
        Task<IReadOnlyDictionary<string, int>> GetConfirmedState();

        /// <summary> Confirm all events </summary>
        Task ConfirmAllPreviouslyRaisedEvents();

    }
}
