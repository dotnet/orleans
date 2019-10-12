
namespace Orleans.Providers.Abstractions
{
    /// <summary>
    /// A cache which supports FiFo evictions
    /// </summary>
    public interface IFiFoEvictableCache<TCachedItem>
        where TCachedItem : struct
    {
        /// <summary>
        /// Remove oldest message in the cache
        /// </summary>
        void RemoveOldestMessage();

        /// <summary>
        /// Newest message in the cache
        /// </summary>
        TCachedItem? Newest { get; }

        /// <summary>
        /// Oldest message in the cache
        /// </summary>
        TCachedItem? Oldest { get; }
    }
}
