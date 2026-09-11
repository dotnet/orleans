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
    public async Task ReadRowsAsync_CancellationAfterFetch_PreservesBufferedRows()
    {
        var first = new Row();
        var second = new Row();
        var rows = new BufferedOnlyRowSet(first, second);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var enumerator = OrleansQueries.ReadRowsAsync(rows, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(first, enumerator.Current);

        cancellation.Cancel();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(second, enumerator.Current);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(0, rows.GetAvailableWithoutFetching());
    }

    [Fact]
    public async Task AwaitAsync_CompletedOperations_PreserveResults()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var completed = Task.FromResult(42);
        cancellation.Cancel();

        Assert.Equal(42, await OrleansQueries.AwaitAsync(completed, cancellation.Token));
        await OrleansQueries.AwaitAsync(Task.CompletedTask, cancellation.Token);
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
