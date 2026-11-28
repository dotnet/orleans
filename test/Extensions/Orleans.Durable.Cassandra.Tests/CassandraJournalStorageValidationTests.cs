using System.Buffers;
using System.Reflection;
using Cassandra;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Journaling;
using Orleans.Storage;
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

    [Theory]
    [InlineData("observed")]
    [InlineData("observed_after_append")]
    [InlineData("read_failure")]
    [InlineData("cancelled")]
    [InlineData("conflict")]
    [InlineData("deleted")]
    public async Task ReplaceAsync_WhenPublicationIsReplayed_CleansOnlyAnInactiveGeneration(string outcome)
    {
        var session = DispatchProxy.Create<ISession, SimulatedSessionProxy>();
        var proxy = (SimulatedSessionProxy)(object)session;
        var options = new CassandraJournalStorageOptions();
        var queries = new CassandraJournalStorageQueries(session, options);
        await queries.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = new CassandraJournalStorage(
            queries, options, new JournalId("replay"), "json", NullLogger<CassandraJournalStorage>.Instance);
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        proxy.Outcome = outcome;
        proxy.Cancellation = cancellation;

        var replace = () => storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 }), cancellation.Token).AsTask();
        if (outcome == "cancelled")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(replace);
        }
        else if (outcome == "read_failure")
        {
            await Assert.ThrowsAsync<TimeoutException>(replace);
        }
        else
        {
            await Assert.ThrowsAsync<InconsistentStateException>(replace);
        }

        Assert.Equal(1, proxy.PublicationAttempts);
        Assert.NotEqual(Guid.Empty, proxy.PublishedGeneration);
        if (outcome is "conflict" or "deleted")
        {
            Assert.NotEqual(proxy.ActiveGeneration, proxy.PublishedGeneration);
            Assert.Contains(proxy.PublishedGeneration, proxy.DeletedGenerations);
        }
        else
        {
            Assert.Equal(proxy.PublishedGeneration, proxy.ActiveGeneration);
            Assert.DoesNotContain(proxy.ActiveGeneration, proxy.DeletedGenerations);
        }
    }

    [Fact]
    public async Task ReplaceAsync_WhenMetadataChangesDuringPublication_RetriesAndDeletesOnlyTheOldGeneration()
    {
        var session = DispatchProxy.Create<ISession, SimulatedSessionProxy>();
        var proxy = (SimulatedSessionProxy)(object)session;
        var options = new CassandraJournalStorageOptions();
        var queries = new CassandraJournalStorageQueries(session, options);
        await queries.InitializeAsync(TestContext.Current.CancellationToken);
        var storage = new CassandraJournalStorage(
            queries, options, new JournalId("metadata-race"), "json", NullLogger<CassandraJournalStorage>.Instance);
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));
        var oldGeneration = proxy.ActiveGeneration;
        proxy.Outcome = "metadata_only";

        await storage.ReplaceAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 }), TestContext.Current.CancellationToken);

        Assert.Equal(2, proxy.PublicationAttempts);
        Assert.Equal(proxy.PublishedGeneration, proxy.ActiveGeneration);
        Assert.Contains(oldGeneration, proxy.DeletedGenerations);
        Assert.DoesNotContain(proxy.ActiveGeneration, proxy.DeletedGenerations);
    }

    [Fact]
    public async Task ConditionalStatements_OverrideIdempotentPreparedStatements()
    {
        var session = DispatchProxy.Create<ISession, SimulatedSessionProxy>();
        var queries = new CassandraJournalStorageQueries(session, new CassandraJournalStorageOptions());
        await queries.InitializeAsync(TestContext.Current.CancellationToken);
        var revision = Guid.NewGuid();
        var generation = Guid.NewGuid();

        Assert.Equal((bool?)false, queries.CreateHeader("journal", generation, "json", [], revision, revision).IsIdempotent);
        Assert.Equal((bool?)false, queries.Append(
            "journal", generation, 0, Guid.NewGuid(), [1], 1, 1, 1, 1, "json", revision, revision, revision, 0).IsIdempotent);
        Assert.Equal((bool?)false, queries.PublishReplacement(
            "journal", generation, 1, 1, "json", revision, revision, revision, generation, 0).IsIdempotent);
        Assert.Equal((bool?)false, queries.UpdateMetadata("journal", [], revision, revision).IsIdempotent);
        Assert.Equal((bool?)false, queries.DeleteHeader("journal", revision).IsIdempotent);
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

    private class SimulatedSessionProxy : DispatchProxy
    {
        private Guid _revision;
        private Guid _contentRevision;
        private long _nextSequence;
        private long _totalLength;
        private string _format = "json";

        public string Outcome { get; set; } = "";
        public CancellationTokenSource? Cancellation { get; set; }
        public Guid ActiveGeneration { get; private set; }
        public Guid PublishedGeneration { get; private set; }
        public int PublicationAttempts { get; private set; }
        public List<Guid> DeletedGenerations { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISession.PrepareAsync))
            {
                return Task.FromResult<PreparedStatement>(new SimulatedPreparedStatement((string)args![0]!));
            }

            if (targetMethod?.Name != nameof(ISession.ExecuteAsync))
            {
                throw new NotSupportedException($"Unexpected session member: {targetMethod?.Name}");
            }

            if (args![0] is not SimulatedBoundStatement statement)
            {
                return Task.FromResult(new RowSet());
            }

            var query = statement.Query;
            var values = statement.Values;
            if (query.Contains("IF NOT EXISTS", StringComparison.Ordinal))
            {
                ActiveGeneration = (Guid)values[3];
                _format = (string)values[4];
                _revision = (Guid)values[6];
                _contentRevision = (Guid)values[7];
                return Applied(true);
            }

            if (query.Contains("SET active_generation", StringComparison.Ordinal))
            {
                PublicationAttempts++;
                PublishedGeneration = (Guid)values[0];
                if (Outcome == "metadata_only" && PublicationAttempts == 1)
                {
                    _revision = Guid.NewGuid();
                    return Applied(false);
                }

                ActiveGeneration = Outcome switch
                {
                    "conflict" => Guid.NewGuid(),
                    "deleted" => Guid.Empty,
                    _ => PublishedGeneration,
                };
                _nextSequence = (long)values[1];
                _totalLength = (long)values[2];
                _format = (string)values[3];
                _revision = (Guid)values[4];
                _contentRevision = (Guid)values[5];
                if (Outcome is "conflict" or "observed_after_append")
                {
                    _revision = Guid.NewGuid();
                    _contentRevision = Guid.NewGuid();
                }

                if (Outcome == "observed_after_append")
                {
                    _nextSequence++;
                    _totalLength++;
                }

                if (Outcome == "cancelled")
                {
                    Cancellation!.Cancel();
                }

                if (Outcome == "metadata_only")
                {
                    return Applied(true);
                }

                // The original publication committed, but its replay returned a failed condition.
                return Applied(false);
            }

            if (query.Contains("SELECT active_generation", StringComparison.Ordinal))
            {
                if (Outcome == "read_failure")
                {
                    return Task.FromException<RowSet>(new TimeoutException("Injected header read failure."));
                }

                if (Outcome == "deleted")
                {
                    return Task.FromResult(new RowSet());
                }

                return Task.FromResult<RowSet>(new SimulatedRowSet(new SimulatedRow(new Dictionary<string, object>
                {
                    ["schema_version"] = 1,
                    ["active_generation"] = ActiveGeneration,
                    ["next_sequence"] = _nextSequence,
                    ["total_length"] = _totalLength,
                    ["append_count"] = 0L,
                    ["append_length"] = 0L,
                    ["journal_format"] = _format,
                    ["caller_metadata"] = new Dictionary<string, string>(),
                    ["revision"] = _revision,
                    ["content_revision"] = _contentRevision,
                })));
            }

            if (query.Contains("DELETE FROM", StringComparison.Ordinal))
            {
                DeletedGenerations.Add((Guid)values[1]);
            }

            return Task.FromResult(new RowSet());
        }

        private static Task<RowSet> Applied(bool applied)
            => Task.FromResult<RowSet>(new SimulatedRowSet(
                new SimulatedRow(new Dictionary<string, object> { ["[applied]"] = applied })));
    }

    private sealed class SimulatedPreparedStatement : PreparedStatement
    {
        private readonly string _query;

        public SimulatedPreparedStatement(string query)
        {
            _query = query;
            SetIdempotence(true);
        }

        public override BoundStatement Bind(params object[] values) => new SimulatedBoundStatement(this, _query, values);
    }

    private sealed class SimulatedBoundStatement(PreparedStatement prepared, string query, object[] values) : BoundStatement(prepared)
    {
        public string Query { get; } = query;
        public object[] Values { get; } = values;
    }

    private sealed class SimulatedRowSet : RowSet
    {
        public SimulatedRowSet(Row row) => RowQueue.Enqueue(row);
    }

    private sealed class SimulatedRow(Dictionary<string, object> values) : Row
    {
        public override T GetValue<T>(string name) => (T)values[name];
    }
}
