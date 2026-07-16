using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Orleans.Reminders.Cosmos;
using Xunit;

namespace Tester.Cosmos.Reminders;

/// <summary>
/// Unit tests for <see cref="FeedIteratorExtensions.DrainAsync{T}"/>. These validate the
/// specific pagination invariant that once tripped a real production bug in
/// <c>CosmosReminderTable.ReadRows</c>: a <see cref="FeedIterator{T}"/> can return an
/// empty page while <see cref="FeedIterator.HasMoreResults"/> remains <c>true</c>
/// (e.g. when the previous page exhausted the RU budget while scanning a partition
/// with no matches). The drain helper must keep iterating past empty pages.
///
/// A live Cosmos DB (or emulator) cannot reliably reproduce this pattern in a
/// deterministic way, so these tests drive the helper with an in-memory
/// <see cref="FeedIterator{T}"/> subclass that plays back a scripted sequence of
/// pages including empty ones.
/// </summary>
public class FeedIteratorExtensionsTests
{
    [Fact]
    public async Task DrainAsync_EmptyPageInMiddle_ContinuesIterating()
    {
        // Simulates the pathological page layout the fix guards against: results,
        // then an empty page while HasMoreResults is still true, then more results.
        // A "break on first empty page" drain would drop the trailing rows.
        var iterator = new FakeFeedIterator<int>(
            new[] { 1, 2, 3 },
            Array.Empty<int>(),
            new[] { 4, 5 });

        var drained = await iterator.DrainAsync();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, drained);
    }

    [Fact]
    public async Task DrainAsync_LeadingEmptyPage_ContinuesIterating()
    {
        // First page empty while HasMoreResults is still true. A break-on-empty
        // implementation would return zero rows even though matches exist further on.
        var iterator = new FakeFeedIterator<int>(
            Array.Empty<int>(),
            new[] { 1, 2, 3 });

        var drained = await iterator.DrainAsync();

        Assert.Equal(new[] { 1, 2, 3 }, drained);
    }

    [Fact]
    public async Task DrainAsync_AllEmptyPages_ReturnsEmpty()
    {
        var iterator = new FakeFeedIterator<int>(
            Array.Empty<int>(),
            Array.Empty<int>(),
            Array.Empty<int>());

        var drained = await iterator.DrainAsync();

        Assert.Empty(drained);
    }

    [Fact]
    public async Task DrainAsync_SinglePage_ReturnsAllItems()
    {
        var iterator = new FakeFeedIterator<int>(new[] { 1, 2, 3, 4, 5 });

        var drained = await iterator.DrainAsync();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, drained);
    }

    [Fact]
    public async Task DrainAsync_TrailingEmptyPage_ReturnsAllItems()
    {
        var iterator = new FakeFeedIterator<int>(
            new[] { 1, 2 },
            Array.Empty<int>());

        var drained = await iterator.DrainAsync();

        Assert.Equal(new[] { 1, 2 }, drained);
    }

    /// <summary>
    /// A <see cref="FeedIterator{T}"/> that plays back a scripted list of pages.
    /// <see cref="HasMoreResults"/> stays <c>true</c> until every scripted page has
    /// been consumed via <see cref="ReadNextAsync(CancellationToken)"/>, so an empty
    /// page never terminates iteration on its own.
    /// </summary>
    private sealed class FakeFeedIterator<T> : FeedIterator<T>
    {
        private readonly Queue<IReadOnlyList<T>> _pages;

        public FakeFeedIterator(params IReadOnlyList<T>[] pages)
        {
            _pages = new Queue<IReadOnlyList<T>>(pages);
        }

        public override bool HasMoreResults => _pages.Count > 0;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            if (_pages.Count == 0)
            {
                throw new InvalidOperationException("ReadNextAsync called after all pages consumed.");
            }

            return Task.FromResult<FeedResponse<T>>(new FakeFeedResponse<T>(_pages.Dequeue()));
        }
    }

    /// <summary>
    /// Minimal <see cref="FeedResponse{T}"/> stub exposing the members the drain
    /// helper actually reads (<see cref="Count"/> and the enumerator). Everything
    /// else returns a safe default so nothing throws on unrelated access.
    /// </summary>
    private sealed class FakeFeedResponse<T> : FeedResponse<T>
    {
        private readonly IReadOnlyList<T> _items;

        public FakeFeedResponse(IReadOnlyList<T> items) => _items = items;

        public override int Count => _items.Count;
        public override string ContinuationToken => null!;
        public override string IndexMetrics => null!;
        public override Headers Headers => null!;
        public override IEnumerable<T> Resource => _items;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override double RequestCharge => 0;
        public override string ActivityId => string.Empty;
        public override string ETag => null!;
        public override CosmosDiagnostics Diagnostics => null!;

        public override IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    }
}
