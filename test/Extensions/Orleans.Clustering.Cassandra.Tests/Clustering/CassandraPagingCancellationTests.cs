using Cassandra;
using Orleans.Clustering.Cassandra;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Clustering;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("Membership")]
[TestCategory("BVT")]
public class CassandraPagingCancellationTests
{
    [Fact]
    public async Task ReadRowsAsync_ConsumesBufferedRowsWithoutSynchronousPaging()
    {
        var first = new Row();
        var second = new Row();
        var rows = new BufferedOnlyRowSet(first, second);
        var result = new List<Row>();

        await foreach (var row in OrleansQueries.ReadRowsAsync(rows, TestContext.Current.CancellationToken))
        {
            result.Add(row);
        }

        Assert.Collection(result, row => Assert.Same(first, row), row => Assert.Same(second, row));
    }

    [Fact]
    public async Task ReadRowsAsync_CancellationStopsBufferedEnumeration()
    {
        var first = new Row();
        var rows = new BufferedOnlyRowSet(first, new Row());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var enumerator = OrleansQueries.ReadRowsAsync(rows, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(first, enumerator.Current);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.Equal(1, rows.GetAvailableWithoutFetching());
    }

    private sealed class BufferedOnlyRowSet : RowSet
    {
        public BufferedOnlyRowSet(params Row[] rows)
        {
            foreach (var row in rows)
            {
                RowQueue.Enqueue(row);
            }
        }

        protected override void PageNext() => throw new InvalidOperationException("Paging must be awaited explicitly.");
    }
}
