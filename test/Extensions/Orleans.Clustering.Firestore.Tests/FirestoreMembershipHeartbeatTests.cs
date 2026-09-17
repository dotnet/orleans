using System.Net;
using System.Reflection;
using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Orleans.Clustering.Firestore.Tests;

[TestSuite("BVT")]
[TestProvider("GoogleCloud")]
[TestArea("Membership")]
[TestCategory("BVT")]
public sealed class FirestoreMembershipHeartbeatTests
{
    private static readonly DateTime Now = DateTime.UnixEpoch.AddHours(2);
    private static readonly string Partition = Utils.SanitizeId("cluster");
    private static readonly string CollectionPath = $"projects/test-project/databases/(default)/documents/Orleans/Cluster/{Partition}";
    private static readonly string VersionPath = $"{CollectionPath}/{Partition}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatAfterCompactionPreservesAbsenceAndVersion(bool deletionRacesCommit)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var path = RowPath(entry);
        if (deletionRacesCommit)
        {
            client.AddRow(entry, DateTime.UnixEpoch);
            client.BeforeCommit = () => client.Documents.Remove(path);
        }

        var table = CreateTable(client);
        var version = client.Documents[VersionPath].Clone();

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(deletionRacesCommit ? 2 : 1, client.Transactions);
        Assert.Equal(deletionRacesCommit ? 3 : 2, client.Reads.Count);
        Assert.Equal(VersionPath, Assert.Single(client.Reads[^1].Documents));
        Assert.Equal(deletionRacesCommit ? 1 : 0, client.AttemptedWrites.Count);
        Assert.Empty(client.CommittedWrites);
        Assert.False(client.Documents.ContainsKey(path));
        Assert.Equal(version, Assert.Single(client.Documents).Value);
    }

    [Theory]
    [InlineData(StatusCode.NotFound, false)]
    [InlineData(StatusCode.PermissionDenied, false)]
    [InlineData(StatusCode.Unavailable, false)]
    [InlineData(StatusCode.NotFound, true)]
    [InlineData(StatusCode.PermissionDenied, true)]
    [InlineData(StatusCode.Unavailable, true)]
    public async Task HeartbeatInfrastructureFailuresRemainVisible(StatusCode status, bool versionRead)
    {
        var failure = new RpcException(new Status(status, "infrastructure-failure"));
        var client = new MembershipClient { ReadFailure = failure, FailVersionRead = versionRead };
        var table = CreateTable(client);

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => table.UpdateIAmAliveAsync(Entry(), TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(
            versionRead ? new[] { RowPath(Entry()), VersionPath } : new[] { RowPath(Entry()) },
            client.Reads.Select(read => Assert.Single(read.Documents)).Distinct());
        Assert.Empty(client.AttemptedWrites);
        Assert.Empty(client.CommittedWrites);
    }

    [Fact]
    public async Task HeartbeatMissingMembershipHistoryFailsClosed()
    {
        var client = new MembershipClient();
        client.Documents.Clear();
        var table = CreateTable(client);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => table.UpdateIAmAliveAsync(Entry(), TestContext.Current.CancellationToken));

        Assert.Equal(2, client.Reads.Count);
        Assert.Empty(client.AttemptedWrites);
        Assert.Empty(client.CommittedWrites);
        Assert.Empty(client.Documents);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HeartbeatPreservesMaximumAndOtherFields(int hours)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, Now).Clone();
        var version = client.Documents[VersionPath].Clone();
        entry.IAmAliveTime = Now.AddHours(hours);
        var table = CreateTable(client);

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        if (hours > 0)
        {
            var write = Assert.Single(client.CommittedWrites);
            Assert.Equal(nameof(SiloInstanceEntity.IAmAliveTime), Assert.Single(write.UpdateMask.FieldPaths));
            original.Fields[nameof(SiloInstanceEntity.IAmAliveTime)] = Time(entry.IAmAliveTime);
            original.UpdateTime = client.Documents[original.Name].UpdateTime;
        }
        else
        {
            Assert.Empty(client.CommittedWrites);
        }

        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Fact]
    public async Task HeartbeatRechecksConcurrentNewerHeartbeat()
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        client.BeforeCommit = () => client.Change(original.Name, nameof(SiloInstanceEntity.IAmAliveTime), Time(Now));

        await CreateTable(client).UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Transactions);
        Assert.Single(client.AttemptedWrites);
        Assert.Empty(client.CommittedWrites);
        original.Fields[nameof(SiloInstanceEntity.IAmAliveTime)] = Time(Now);
        original.UpdateTime = client.Documents[original.Name].UpdateTime;
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task FullRowUpdatePreservesMaximumHeartbeatAndAtomicallyUpdatesVersion(int hours)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, Now).Clone();
        entry.IAmAliveTime = Now.AddHours(hours);
        entry.Status = SiloStatus.Dead;
        entry.AddSuspector(SiloAddress.New(IPAddress.Loopback, 11112, 1), Now);
        var table = CreateTable(client);

        Assert.True(await table.UpdateRowAsync(entry, ETag(original), NextVersion(client), TestContext.Current.CancellationToken));

        var commit = Assert.Single(client.Commits, commit => commit.Writes.Count > 0);
        Assert.Equal(2, commit.Writes.Count);
        Assert.Equal(new[] { original.Name, VersionPath }, commit.Writes.Select(write => write.Update.Name));
        Assert.All(commit.Writes, write => Assert.Equal(original.UpdateTime, write.CurrentDocument.UpdateTime));
        var row = client.Documents[original.Name];
        Assert.Equal(Time(hours > 0 ? entry.IAmAliveTime : Now), row.Fields[nameof(SiloInstanceEntity.IAmAliveTime)]);
        Assert.Equal((long)SiloStatus.Dead, row.Fields[nameof(SiloInstanceEntity.Status)].IntegerValue);
        Assert.Equal(Time(Now), Assert.Single(row.Fields[nameof(SiloInstanceEntity.SuspectingTimes)].ArrayValue.Values));
        Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
        Assert.Equal(8, row.Fields[nameof(SiloInstanceEntity.MembershipVersion)].IntegerValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRowUpdateRejectsConcurrentHeartbeatOrVersionChange(bool versionChanges)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        var table = CreateTable(client);
        client.BeforeCommit = () => client.Change(
            versionChanges ? VersionPath : original.Name,
            versionChanges ? nameof(ClusterVersionEntity.MembershipVersion) : nameof(SiloInstanceEntity.IAmAliveTime),
            versionChanges ? new Value { IntegerValue = 8 } : Time(Now));

        Assert.False(await table.UpdateRowAsync(entry, ETag(original), NextVersion(client), TestContext.Current.CancellationToken));

        Assert.Empty(client.CommittedWrites);
        if (versionChanges)
        {
            Assert.Equal(original, client.Documents[original.Name]);
            Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
        }
        else
        {
            Assert.Equal(Time(Now), client.Documents[original.Name].Fields[nameof(SiloInstanceEntity.IAmAliveTime)]);
            Assert.Equal(version, client.Documents[VersionPath]);
        }
    }

    [Theory]
    [InlineData(SiloStatus.None)]
    [InlineData(SiloStatus.Created)]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Stopping)]
    public async Task CleanupRetainsAllNonDeadRows(SiloStatus status)
    {
        var client = new MembershipClient();
        var entry = Entry();
        entry.Status = status;
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken);

        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
        AssertTransactionalQueries(client);
    }

    [Theory]
    [InlineData(nameof(SiloInstanceEntity.StartTime), -1)]
    [InlineData(nameof(SiloInstanceEntity.StartTime), 0)]
    [InlineData(nameof(SiloInstanceEntity.StartTime), 1)]
    [InlineData(nameof(SiloInstanceEntity.IAmAliveTime), -1)]
    [InlineData(nameof(SiloInstanceEntity.IAmAliveTime), 0)]
    [InlineData(nameof(SiloInstanceEntity.IAmAliveTime), 1)]
    [InlineData(nameof(SiloInstanceEntity.SuspectingTimes), -1)]
    [InlineData(nameof(SiloInstanceEntity.SuspectingTimes), 0)]
    [InlineData(nameof(SiloInstanceEntity.SuspectingTimes), 1)]
    public async Task CleanupUsesLatestTimestampAndStrictTickCutoff(string field, int cutoffTicks)
    {
        var client = new MembershipClient();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        var document = client.AddRow(entry, DateTime.UnixEpoch);
        document.Fields[field] = field == nameof(SiloInstanceEntity.SuspectingTimes)
            ? new Value { ArrayValue = new ArrayValue { Values = { Time(Now), Time(DateTime.UnixEpoch) } } }
            : Time(Now);
        var original = document.Clone();
        var version = client.Documents[VersionPath].Clone();

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now.AddTicks(cutoffTicks), TestContext.Current.CancellationToken);

        Assert.Equal(cutoffTicks <= 0, client.Documents.ContainsKey(original.Name));
        Assert.Equal(version, client.Documents[VersionPath]);
        if (cutoffTicks > 0)
        {
            var write = Assert.Single(client.CommittedWrites);
            Assert.Equal(original.Name, write.Delete);
            Assert.Equal(original.UpdateTime, write.CurrentDocument.UpdateTime);
        }
        else
        {
            Assert.Empty(client.CommittedWrites);
            Assert.Equal(original, client.Documents[original.Name]);
        }

        AssertTransactionalQueries(client);
    }

    [Theory]
    [InlineData(nameof(SiloInstanceEntity.IAmAliveTime))]
    [InlineData(nameof(SiloInstanceEntity.Status))]
    [InlineData(nameof(SiloInstanceEntity.SuspectingTimes))]
    public async Task CleanupRechecksConcurrentHeartbeatStatusAndVotes(string field)
    {
        var client = new MembershipClient();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        var value = field switch
        {
            nameof(SiloInstanceEntity.Status) => new Value { IntegerValue = (int)SiloStatus.Active },
            nameof(SiloInstanceEntity.SuspectingTimes) => new Value { ArrayValue = new ArrayValue { Values = { Time(Now) } } },
            _ => Time(Now)
        };
        client.BeforeCommit = () => client.Change(original.Name, field, value);

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Transactions);
        var attempted = Assert.Single(client.AttemptedWrites);
        Assert.Equal(original.Name, attempted.Delete);
        Assert.Equal(original.UpdateTime, attempted.CurrentDocument.UpdateTime);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(value, client.Documents[original.Name].Fields[field]);
        Assert.Equal(version, client.Documents[VersionPath]);
        AssertTransactionalQueries(client);
    }

    [Fact]
    public async Task CleanupUsesFullDeleteBatchesAndPreservesVersionAtMaximum()
    {
        var client = new MembershipClient();
        client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)] = new Value { IntegerValue = int.MaxValue };
        var version = client.Documents[VersionPath].Clone();
        for (var i = 0; i <= FirestoreDataManager.MaxBatchSize; i++)
        {
            var entry = Entry(i + 1);
            entry.Status = SiloStatus.Dead;
            client.AddRow(entry, DateTime.UnixEpoch);
        }

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { FirestoreDataManager.MaxBatchSize, 1 }, client.Commits.Where(c => c.Writes.Count > 0).Select(c => c.Writes.Count));
        Assert.Equal(FirestoreDataManager.MaxBatchSize + 1, client.CommittedWrites.Count);
        Assert.All(client.CommittedWrites, write => Assert.Equal(Write.OperationOneofCase.Delete, write.OperationCase));
        Assert.Equal(version, Assert.Single(client.Documents).Value);
        AssertTransactionalQueries(client);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipReadsUseOneTransactionForRowsAndVersion(bool readAll)
    {
        var client = new MembershipClient();
        var entry = Entry();
        client.AddRow(entry, Now);
        var table = CreateTable(client);

        var result = readAll
            ? await table.ReadAllAsync(TestContext.Current.CancellationToken)
            : await table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken);

        Assert.Equal(7, result.Version.Version);
        Assert.Equal(entry.SiloAddress, Assert.Single(result.Members).Item1.SiloAddress);
        Assert.Equal(Now, Assert.Single(result.Members).Item1.IAmAliveTime);
        Assert.Equal(1, client.Transactions);
        Assert.Empty(client.CommittedWrites);
        if (readAll)
        {
            AssertTransactionalQueries(client);
        }
        else
        {
            Assert.Equal(2, client.Reads.Count);
            Assert.All(client.Reads, read => Assert.Equal(client.Commits.Single().Transaction, read.Transaction));
        }
    }

    [Fact]
    public async Task InsertAtomicallyCreatesRowAndAdvancesVersion()
    {
        var client = new MembershipClient();
        var entry = Entry();

        Assert.True(await CreateTable(client).InsertRowAsync(entry, NextVersion(client), TestContext.Current.CancellationToken));

        var commit = Assert.Single(client.Commits);
        Assert.Equal(2, commit.Writes.Count);
        Assert.Equal(RowPath(entry), commit.Writes[0].Update.Name);
        Assert.False(commit.Writes[0].CurrentDocument.Exists);
        Assert.Equal(VersionPath, commit.Writes[1].Update.Name);
        Assert.Equal(Timestamp.FromDateTime(DateTime.UnixEpoch), commit.Writes[1].CurrentDocument.UpdateTime);
        Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
        Assert.Equal(8, client.Documents[RowPath(entry)].Fields[nameof(SiloInstanceEntity.MembershipVersion)].IntegerValue);
    }

    [Fact]
    public async Task DeleteMembershipEntriesPreservesOtherCluster()
    {
        var client = new MembershipClient();
        client.AddRow(Entry(), Now);
        var other = client.Documents[VersionPath].Clone();
        other.Name = VersionPath.Replace($"/{Partition}/", "/other-cluster/", StringComparison.Ordinal);
        client.Documents.Add(other.Name, other);

        await CreateTable(client).DeleteMembershipTableEntriesAsync("cluster", TestContext.Current.CancellationToken);

        Assert.Equal(other, Assert.Single(client.Documents).Value);
        Assert.Equal(2, client.CommittedWrites.Count);
        Assert.All(client.CommittedWrites, write => Assert.StartsWith($"{CollectionPath}/", write.Delete));
    }

    private static void AssertTransactionalQueries(MembershipClient client)
    {
        Assert.NotEmpty(client.Queries);
        Assert.All(client.Queries, query =>
        {
            Assert.False(query.Transaction.IsEmpty);
            Assert.Equal(CollectionPath, $"{query.Parent}/{Assert.Single(query.StructuredQuery.From).CollectionId}");
            Assert.Contains(client.Commits, commit => commit.Transaction == query.Transaction);
        });
    }

    private static MembershipEntry Entry(int generation = 1) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, generation),
        HostName = "localhost",
        SiloName = $"silo-{generation}",
        Status = SiloStatus.Active,
        StartTime = DateTime.UnixEpoch,
        IAmAliveTime = DateTime.UnixEpoch.AddHours(1)
    };

    private static string RowPath(MembershipEntry entry) => $"{CollectionPath}/{entry.SiloAddress.ToParsableString()}";
    private static Value Time(DateTime time) => new() { TimestampValue = Timestamp.FromDateTime(time) };
    private static string ETag(Document document) => Utils.FormatTimestamp(Google.Cloud.Firestore.Timestamp.FromDateTime(document.UpdateTime.ToDateTime()));
    private static TableVersion NextVersion(MembershipClient client) => new(8, ETag(client.Documents[VersionPath]));

    private static FirestoreMembershipTable CreateTable(MembershipClient client)
    {
        var options = new FirestoreOptions { ProjectId = "test-project", EmulatorHost = "127.0.0.1:1" };
        var storage = new FirestoreDataManager("Cluster", Partition, options, NullLogger<FirestoreDataManager>.Instance);
        typeof(FirestoreDataManager).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(storage, FirestoreDb.Create(options.ProjectId, client));
        var table = new FirestoreMembershipTable(
            NullLoggerFactory.Instance, Options.Create(options), Options.Create(new ClusterOptions { ClusterId = "cluster" }));
        typeof(FirestoreMembershipTable).GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, storage);
        return table;
    }

    private sealed class MembershipClient : FirestoreClient
    {
        private readonly Dictionary<ByteString, Dictionary<string, Document>> _snapshots = [];
        private readonly Dictionary<ByteString, HashSet<string>> _readSets = [];
        private int _revision;

        public override FirestoreSettings Settings => FirestoreSettings.GetDefault();
        public Dictionary<string, Document> Documents { get; } = [];
        public Action? BeforeCommit { get; set; }
        public RpcException? ReadFailure { get; init; }
        public bool FailVersionRead { get; init; }
        public int Transactions { get; private set; }
        public List<BatchGetDocumentsRequest> Reads { get; } = [];
        public List<RunQueryRequest> Queries { get; } = [];
        public List<CommitRequest> Commits { get; } = [];
        public List<Write> AttemptedWrites { get; } = [];
        public List<Write> CommittedWrites { get; } = [];

        public MembershipClient()
        {
            var version = NewDocument(VersionPath);
            version.Fields[nameof(ClusterVersionEntity.MembershipVersion)] = new Value { IntegerValue = 7 };
            Documents.Add(version.Name, version);
        }

        public Document AddRow(MembershipEntry entry, DateTime heartbeat)
        {
            var row = NewDocument(RowPath(entry));
            row.Fields[nameof(SiloInstanceEntity.Address)] = new Value { StringValue = entry.SiloAddress.Endpoint.Address.ToString() };
            row.Fields[nameof(SiloInstanceEntity.Port)] = new Value { IntegerValue = entry.SiloAddress.Endpoint.Port };
            row.Fields[nameof(SiloInstanceEntity.Generation)] = new Value { IntegerValue = entry.SiloAddress.Generation };
            row.Fields[nameof(SiloInstanceEntity.HostName)] = new Value { StringValue = entry.HostName };
            row.Fields[nameof(SiloInstanceEntity.SiloName)] = new Value { StringValue = entry.SiloName };
            row.Fields[nameof(SiloInstanceEntity.Status)] = new Value { IntegerValue = (int)entry.Status };
            row.Fields[nameof(SiloInstanceEntity.StartTime)] = Time(entry.StartTime);
            row.Fields[nameof(SiloInstanceEntity.IAmAliveTime)] = Time(heartbeat);
            row.Fields[nameof(SiloInstanceEntity.MembershipVersion)] = new Value { IntegerValue = 7 };
            Documents.Add(row.Name, row);
            return row;
        }

        public void Change(string name, string field, Value value)
        {
            Documents[name].Fields[field] = value;
            Documents[name].UpdateTime = NextUpdateTime();
        }

        public override Task<BeginTransactionResponse> BeginTransactionAsync(BeginTransactionRequest request, CallSettings? callSettings = null)
        {
            var transaction = ByteString.CopyFromUtf8($"transaction-{++Transactions}");
            _snapshots.Add(transaction, Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()));
            _readSets.Add(transaction, []);
            return Task.FromResult(new BeginTransactionResponse { Transaction = transaction });
        }

        public override BatchGetDocumentsStream BatchGetDocuments(BatchGetDocumentsRequest request, CallSettings? callSettings = null)
        {
            Reads.Add(request.Clone());
            var name = Assert.Single(request.Documents);
            if (ReadFailure is { } failure && (name == VersionPath) == FailVersionRead)
            {
                throw failure;
            }

            Assert.False(request.Transaction.IsEmpty);
            _readSets[request.Transaction].Add(name);
            var response = new BatchGetDocumentsResponse { ReadTime = Timestamp.FromDateTime(Now) };
            if (_snapshots[request.Transaction].TryGetValue(name, out var document))
            {
                response.Found = document.Clone();
            }
            else
            {
                response.Missing = name;
            }

            return new DocumentStream(response);
        }

        public override RunQueryStream RunQuery(RunQueryRequest request, CallSettings? callSettings = null)
        {
            Queries.Add(request.Clone());
            var collection = $"{request.Parent}/{Assert.Single(request.StructuredQuery.From).CollectionId}/";
            var documents = request.Transaction.IsEmpty ? Documents : _snapshots[request.Transaction];
            var responses = documents.Values.Where(document => document.Name.StartsWith(collection, StringComparison.Ordinal))
                .Select(document =>
                {
                    if (!request.Transaction.IsEmpty)
                    {
                        _readSets[request.Transaction].Add(document.Name);
                    }

                    return new RunQueryResponse { Document = document.Clone(), ReadTime = Timestamp.FromDateTime(Now) };
                }).ToArray();
            return new QueryStream(responses);
        }

        public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? callSettings = null)
        {
            Commits.Add(request.Clone());
            AttemptedWrites.AddRange(request.Writes.Select(write => write.Clone()));
            var beforeCommit = BeforeCommit;
            BeforeCommit = null;
            beforeCommit?.Invoke();
            if (!request.Transaction.IsEmpty && _readSets[request.Transaction].Any(name =>
                !Equals(_snapshots[request.Transaction].GetValueOrDefault(name), Documents.GetValueOrDefault(name))))
            {
                return Task.FromException<CommitResponse>(new RpcException(new Status(StatusCode.Aborted, "Read document changed.")));
            }

            foreach (var write in request.Writes)
            {
                var name = write.OperationCase == Google.Cloud.Firestore.V1.Write.OperationOneofCase.Delete ? write.Delete : write.Update.Name;
                var current = Documents.GetValueOrDefault(name);
                if (write.CurrentDocument is { } precondition
                    && (precondition.ConditionTypeCase == Google.Cloud.Firestore.V1.Precondition.ConditionTypeOneofCase.UpdateTime
                        ? current?.UpdateTime != precondition.UpdateTime
                        : precondition.Exists != (current is not null)))
                {
                    return Task.FromException<CommitResponse>(new RpcException(new Status(StatusCode.FailedPrecondition, "Write precondition failed.")));
                }
            }

            var response = new CommitResponse { CommitTime = NextUpdateTime() };
            foreach (var write in request.Writes)
            {
                if (write.OperationCase == Google.Cloud.Firestore.V1.Write.OperationOneofCase.Delete)
                {
                    Documents.Remove(write.Delete);
                }
                else
                {
                    var document = Documents.GetValueOrDefault(write.Update.Name)?.Clone() ?? NewDocument(write.Update.Name);
                    foreach (var field in write.Update.Fields)
                    {
                        document.Fields[field.Key] = field.Value.Clone();
                    }

                    document.UpdateTime = response.CommitTime;
                    Documents[document.Name] = document;
                }

                response.WriteResults.Add(new Google.Cloud.Firestore.V1.WriteResult { UpdateTime = response.CommitTime });
                CommittedWrites.Add(write.Clone());
            }

            return Task.FromResult(response);
        }

        public override Task RollbackAsync(RollbackRequest request, CallSettings? callSettings = null) => Task.CompletedTask;

        private Timestamp NextUpdateTime() => Timestamp.FromDateTime(Now.AddSeconds(++_revision));

        private static Document NewDocument(string name) => new()
        {
            Name = name,
            CreateTime = Timestamp.FromDateTime(DateTime.UnixEpoch),
            UpdateTime = Timestamp.FromDateTime(DateTime.UnixEpoch)
        };
    }

    private sealed class DocumentStream(BatchGetDocumentsResponse response) : FirestoreClient.BatchGetDocumentsStream
    {
        public override AsyncServerStreamingCall<BatchGetDocumentsResponse> GrpcCall { get; } = CreateStream([response]);
    }

    private sealed class QueryStream(IEnumerable<RunQueryResponse> responses) : FirestoreClient.RunQueryStream
    {
        public override AsyncServerStreamingCall<RunQueryResponse> GrpcCall { get; } = CreateStream(responses);
    }

    private static AsyncServerStreamingCall<T> CreateStream<T>(IEnumerable<T> responses) => new(
        new ResponseReader<T>(responses), Task.FromResult(new global::Grpc.Core.Metadata()), () => Status.DefaultSuccess, () => new global::Grpc.Core.Metadata(), () => { });

    private sealed class ResponseReader<T>(IEnumerable<T> responses) : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _responses = responses.GetEnumerator();
        public T Current => _responses.Current;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.MoveNext());
        }
    }
}
