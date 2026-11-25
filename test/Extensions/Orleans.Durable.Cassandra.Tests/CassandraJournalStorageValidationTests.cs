using System.Buffers;
using System.Reflection;
using Cassandra;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Journaling;
using TestExtensions;
using Xunit;

namespace Tester.Cassandra.Journaling;

[TestSuite("BVT")]
[TestProvider("Cassandra")]
[TestArea("Journaling")]
[TestCategory("BVT")]
[TestCategory("Cassandra")]
public sealed class CassandraJournalStorageValidationTests
{
    [Fact]
    public async Task WritesBeforeReadOrCreateThrowWithoutContactingCassandra()
    {
        var storage = CreateStorage();
        var value = new ReadOnlySequence<byte>(new byte[] { 1 });
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.AppendAsync(value, cancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.ReplaceAsync(value, cancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.DeleteAsync(cancellationToken).AsTask());
    }

    [Fact]
    public async Task EmptyAppendThrowsWithoutContactingCassandra()
    {
        var storage = CreateStorage();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => storage.AppendAsync(ReadOnlySequence<byte>.Empty, TestContext.Current.CancellationToken).AsTask());
    }

    private static CassandraJournalStorage CreateStorage()
    {
        var session = DispatchProxy.Create<ISession, CassandraJournalContainerTests.TestSessionProxy>();
        var options = new CassandraJournalStorageOptions();
        return new CassandraJournalStorage(
            new CassandraJournalStorageQueries(session, options),
            options,
            new JournalId("validation"),
            "json",
            NullLogger<CassandraJournalStorage>.Instance);
    }
}
