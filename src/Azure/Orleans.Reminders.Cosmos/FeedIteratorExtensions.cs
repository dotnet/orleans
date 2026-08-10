using System.Threading;

namespace Orleans.Reminders.Cosmos;

internal static class FeedIteratorExtensions
{
    /// <summary>
    /// Fully drains a Cosmos DB <see cref="FeedIterator{T}"/>, collecting every item across
    /// all pages. Empty pages are skipped but do not terminate iteration: <see
    /// cref="FeedIterator.HasMoreResults"/> may remain <c>true</c> after an empty
    /// <see cref="FeedIterator{T}.ReadNextAsync(System.Threading.CancellationToken)"/>
    /// result (for example when the previous page consumed the RU budget while scanning a
    /// partition with no matching items), so iteration must continue until
    /// <c>HasMoreResults</c> is <c>false</c>.
    /// </summary>
    public static async Task<List<T>> ToListAsync<T>(
        this FeedIterator<T> iterator,
        CancellationToken cancellationToken = default)
    {
        var items = new List<T>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (page is { Count: > 0 })
            {
                items.AddRange(page);
            }
        }

        return items;
    }
}
