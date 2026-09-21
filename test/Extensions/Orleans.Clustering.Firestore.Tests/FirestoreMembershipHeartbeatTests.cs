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
    [InlineData(0)]
    [InlineData(1)]
    public async Task HeartbeatUsesOneTimestampOnlyWrite(int hours)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, Now).Clone();
        var version = client.Documents[VersionPath].Clone();
        var other = client.AddRow(Entry(2), Now).Clone();
        entry.IAmAliveTime = Now.AddHours(hours);
        entry.HostName = "stale-host";
        entry.SiloName = "stale-silo";
        entry.Status = SiloStatus.Joining;
        entry.StartTime = Now;
        entry.AddSuspector(SiloAddress.New(IPAddress.Loopback, 11112, 1), Now);

        await CreateTable(client).UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        AssertSingleHeartbeatWrite(client, entry);
        Assert.Single(client.CommittedWrites);
        original.Fields[nameof(SiloInstanceEntity.IAmAliveTime)] = Time(entry.IAmAliveTime);
        original.UpdateTime = client.Documents[original.Name].UpdateTime;
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
        Assert.Equal(other, client.Documents[other.Name]);
    }

    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.Aborted)]
    [InlineData(StatusCode.FailedPrecondition)]
    [InlineData(StatusCode.Cancelled)]
    public async Task HeartbeatNativeWriteFailuresPropagate(StatusCode status)
    {
        var failure = new RpcException(new Status(status, "write-failure"));
        var client = new MembershipClient { CommitFailure = failure };
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        var table = CreateTable(client);

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        AssertSingleHeartbeatWrite(client, entry);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Fact]
    public async Task HeartbeatPreCancellationIssuesNoRequests()
    {
        var client = new MembershipClient();
        var table = CreateTable(client);
        var cancellationToken = new CancellationToken(canceled: true);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => table.UpdateIAmAliveAsync(Entry(), cancellationToken));

        Assert.Equal(cancellationToken, exception.CancellationToken);
        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        Assert.Empty(client.Commits);
        Assert.Empty(client.AttemptedWrites);
    }

    [Fact]
    public async Task HeartbeatPassesCancellationToNativeWrite()
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        client.BeforeCommit = cancellation.Cancel;
        var table = CreateTable(client);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => table.UpdateIAmAliveAsync(entry, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, client.CommitCancellationToken);
        AssertSingleHeartbeatWrite(client, entry);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Theory]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task HeartbeatClassifiesNativeFailureUsingCallerCancellation(StatusCode status)
    {
        var failure = new RpcException(new Status(status, "native-write-failure"));
        var client = new MembershipClient { CommitFailure = failure };
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        client.BeforeCommit = cancellation.Cancel;
        var table = CreateTable(client);

        if (status == StatusCode.Cancelled)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => table.UpdateIAmAliveAsync(entry, cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Same(failure, exception.InnerException);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<RpcException>(
                () => table.UpdateIAmAliveAsync(entry, cancellation.Token));
            Assert.Same(failure, exception);
        }

        Assert.Equal(cancellation.Token, client.CommitCancellationToken);
        AssertSingleHeartbeatWrite(client, entry);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Fact]
    public async Task HeartbeatPreservesConcurrentVersionedFields()
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        Dictionary<string, Document>? concurrentState = null;
        client.BeforeCommit = () =>
        {
            client.Change(original.Name, nameof(SiloInstanceEntity.Status), new Value { IntegerValue = (int)SiloStatus.ShuttingDown });
            client.Change(original.Name, nameof(SiloInstanceEntity.SuspectingTimes), new Value { ArrayValue = new ArrayValue { Values = { Time(Now) } } });
            client.Change(original.Name, nameof(SiloInstanceEntity.MembershipVersion), new Value { IntegerValue = 8 });
            client.Change(VersionPath, nameof(ClusterVersionEntity.MembershipVersion), new Value { IntegerValue = 8 });
            concurrentState = client.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        };

        await CreateTable(client).UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        AssertSingleHeartbeatWrite(client, entry);
        Assert.Single(client.CommittedWrites);
        Assert.NotNull(concurrentState);
        var expectedRow = concurrentState[original.Name];
        expectedRow.Fields[nameof(SiloInstanceEntity.IAmAliveTime)] = Time(entry.IAmAliveTime);
        expectedRow.UpdateTime = client.Documents[original.Name].UpdateTime;
        Assert.Equal(concurrentState.Count, client.Documents.Count);
        Assert.All(concurrentState, pair => Assert.Equal(pair.Value, client.Documents[pair.Key]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipReadsReturnEachRowETagAndPreserveTableVersionAcrossHeartbeat(bool readAll)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var other = client.AddRow(Entry(2), DateTime.UnixEpoch);
        other.UpdateTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(2));
        client.Documents[VersionPath].UpdateTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(1));
        var table = CreateTable(client);
        var before = await Read();
        Assert.Equal(ETag(original), before.TryGet(entry.SiloAddress)!.Item2);
        Assert.NotEqual(before.Version.VersionEtag, before.TryGet(entry.SiloAddress)!.Item2);
        if (readAll)
        {
            Assert.Equal(ETag(other), before.TryGet(Entry(2).SiloAddress)!.Item2);
        }

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        var after = await Read();
        Assert.NotEqual(original.UpdateTime, client.Documents[original.Name].UpdateTime);
        Assert.Equal(before.Version.Version, after.Version.Version);
        Assert.Equal(before.Version.VersionEtag, after.Version.VersionEtag);
        Assert.All(after.Members, row => Assert.Equal(ETag(client.Documents[RowPath(row.Item1)]), row.Item2));
        Assert.NotEqual(before.TryGet(entry.SiloAddress)!.Item2, after.TryGet(entry.SiloAddress)!.Item2);
        if (readAll)
        {
            Assert.Equal(before.TryGet(Entry(2).SiloAddress)!.Item2, after.TryGet(Entry(2).SiloAddress)!.Item2);
        }
        Assert.Equal(entry.IAmAliveTime, after.TryGet(entry.SiloAddress)!.Item1.IAmAliveTime);

        Task<MembershipTableData> Read() => readAll
            ? table.ReadAllAsync(TestContext.Current.CancellationToken)
            : table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FullRowUpdateAfterHeartbeatUsesOriginalRowETagAndTableVersion()
    {
        var client = new MembershipClient();
        var entry = Entry();
        client.AddRow(entry, DateTime.UnixEpoch);
        client.Documents[VersionPath].UpdateTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(1));
        var table = CreateTable(client);
        var snapshot = await table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken);
        var row = Assert.Single(snapshot.Members);
        row.Item1.Status = SiloStatus.ShuttingDown;
        entry.IAmAliveTime = Now;

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);
        Assert.Equal(Time(Now), client.Documents[RowPath(entry)].Fields[nameof(SiloInstanceEntity.IAmAliveTime)]);
        Assert.NotEqual(row.Item2, ETag(client.Documents[RowPath(entry)]));
        Assert.NotEqual(row.Item2, snapshot.Version.VersionEtag);

        Assert.True(await table.UpdateRowAsync(
            row.Item1, row.Item2, snapshot.Version.Next(), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Equal(2, Assert.Single(client.Reads).Documents.Count);
        Assert.Empty(client.Queries);
        Assert.Equal(new[] { 1, 2 }, client.Commits.Select(commit => commit.Writes.Count));
        Assert.Equal((long)SiloStatus.ShuttingDown, client.Documents[RowPath(entry)].Fields[nameof(SiloInstanceEntity.Status)].IntegerValue);
        Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
        Assert.Equal(8, client.Documents[RowPath(entry)].Fields[nameof(SiloInstanceEntity.MembershipVersion)].IntegerValue);
    }

    [Fact]
    public async Task FullRowUpdateSucceedsWhenHeartbeatRacesCommit()
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var token = ETag(original);
        client.BeforeCommit = () => client.Change(original.Name, nameof(SiloInstanceEntity.IAmAliveTime), Time(Now));

        Assert.True(await CreateTable(client).UpdateRowAsync(
            entry, token, NextVersion(client), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        Assert.Single(client.Commits);
        Assert.Equal(2, client.CommittedWrites.Count);
        Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRowUpdateRejectsConcurrentCanonicalChangesWithoutSideEffects(bool sameRow)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var other = client.AddRow(Entry(2), DateTime.UnixEpoch);
        var token = ETag(original);
        var table = CreateTable(client);
        Dictionary<string, Document>? concurrentState = null;
        client.BeforeCommit = () =>
        {
            var name = sameRow ? original.Name : other.Name;
            client.Change(name, nameof(SiloInstanceEntity.Status), new Value { IntegerValue = (int)SiloStatus.Dead });
            client.Change(name, nameof(SiloInstanceEntity.MembershipVersion), new Value { IntegerValue = 8 });
            client.Change(VersionPath, nameof(ClusterVersionEntity.MembershipVersion), new Value { IntegerValue = 8 });
            concurrentState = client.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        };

        Assert.False(await table.UpdateRowAsync(entry, token, NextVersion(client), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        Assert.Single(client.Commits);
        Assert.Equal(2, client.AttemptedWrites.Count);
        Assert.Empty(client.CommittedWrites);
        Assert.NotNull(concurrentState);
        Assert.Equal(concurrentState.Count, client.Documents.Count);
        Assert.All(concurrentState, pair => Assert.Equal(pair.Value, client.Documents[pair.Key]));
    }

    [Fact]
    public async Task FullRowUpdateUsesOneAtomicCommitWithCanonicalPreconditions()
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, Now).Clone();
        client.Documents[original.Name].UpdateTime = Timestamp.FromDateTime(Now);
        var version = client.Documents[VersionPath].Clone();
        entry.Status = SiloStatus.ShuttingDown;
        entry.AddSuspector(SiloAddress.New(IPAddress.Loopback, 11112, 1), Now);

        Assert.True(await CreateTable(client).UpdateRowAsync(
            entry, ETag(client.Documents[original.Name]), NextVersion(client), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        var commit = Assert.Single(client.Commits);
        Assert.True(commit.Transaction.IsEmpty);
        Assert.Equal(2, commit.Writes.Count);
        Assert.Equal(new[] { original.Name, VersionPath }, commit.Writes.Select(write => write.Update.Name));
        Assert.Equal(Google.Cloud.Firestore.V1.Precondition.ConditionTypeOneofCase.Exists, commit.Writes[0].CurrentDocument.ConditionTypeCase);
        Assert.True(commit.Writes[0].CurrentDocument.Exists);
        Assert.Equal(version.UpdateTime, commit.Writes[1].CurrentDocument.UpdateTime);
        var updated = client.Documents[original.Name];
        Assert.Equal((long)SiloStatus.ShuttingDown, updated.Fields[nameof(SiloInstanceEntity.Status)].IntegerValue);
        Assert.Equal(Time(Now), Assert.Single(updated.Fields[nameof(SiloInstanceEntity.SuspectingTimes)].ArrayValue.Values));
        Assert.Equal(8, updated.Fields[nameof(SiloInstanceEntity.MembershipVersion)].IntegerValue);
        Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
    }

    [Fact]
    public async Task FullRowUpdateAcceptsDistinctRowAndTableETags()
    {
        var client = new MembershipClient();
        var entry = Entry();
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var token = ETag(original);
        client.Change(VersionPath, nameof(ClusterVersionEntity.MembershipVersion), new Value { IntegerValue = 8 });
        var version = client.Documents[VersionPath].Clone();

        Assert.True(await CreateTable(client).UpdateRowAsync(
            entry, token, new TableVersion(9, ETag(version)), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        Assert.Single(client.Commits);
        Assert.Equal(2, client.CommittedWrites.Count);
        Assert.Equal(9, client.Documents[original.Name].Fields[nameof(SiloInstanceEntity.MembershipVersion)].IntegerValue);
        Assert.Equal(9, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
    }

    [Fact]
    public async Task FullRowUpdateRejectsStaleTableVersionWithCurrentRowETag()
    {
        var client = new MembershipClient();
        var entry = Entry();
        var document = client.AddRow(entry, Now);
        var staleVersion = NextVersion(client);
        client.Change(document.Name, nameof(SiloInstanceEntity.Status), new Value { IntegerValue = (int)SiloStatus.ShuttingDown });
        client.Change(VersionPath, nameof(ClusterVersionEntity.MembershipVersion), new Value { IntegerValue = 8 });
        var original = document.Clone();
        var version = client.Documents[VersionPath].Clone();

        Assert.False(await CreateTable(client).UpdateRowAsync(
            entry, ETag(original), staleVersion, TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        Assert.Single(client.Commits);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRowUpdateRequiresExistingRow(bool deletionRacesCommit)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var version = client.Documents[VersionPath].Clone();
        if (deletionRacesCommit)
        {
            client.AddRow(entry, DateTime.UnixEpoch);
            client.BeforeCommit = () => client.Documents.Remove(RowPath(entry));
        }

        Assert.False(await CreateTable(client).UpdateRowAsync(
            entry, ETag(version), NextVersion(client), TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        Assert.Single(client.Commits);
        Assert.Equal(2, client.AttemptedWrites.Count);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(version, Assert.Single(client.Documents).Value);
    }

    [Fact]
    public async Task FullRowUpdatePropagatesNativePermissionFailure()
    {
        var failure = new RpcException(new Status(StatusCode.PermissionDenied, "permission-denied"));
        var client = new MembershipClient { CommitFailure = failure };
        var entry = Entry();
        var original = client.AddRow(entry, Now).Clone();
        var version = client.Documents[VersionPath].Clone();

        var exception = await Assert.ThrowsAsync<RpcException>(() => CreateTable(client).UpdateRowAsync(
            entry, ETag(version), NextVersion(client), TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Single(client.Commits);
        Assert.Equal(2, client.AttemptedWrites.Count);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public async Task NativeFakeCommitComparesTimestampValues(int secondsDelta, int nanosDelta, bool matches)
    {
        var client = new MembershipClient();
        var original = client.AddRow(Entry(), Now).Clone();
        var version = client.Documents[VersionPath].Clone();
        var updateTime = original.UpdateTime.Clone();
        updateTime.Seconds += secondsDelta;
        updateTime.Nanos += nanosDelta;
        Assert.NotSame(client.Documents[original.Name].UpdateTime, updateTime);
        var request = new CommitRequest
        {
            Writes =
            {
                new Write
                {
                    Delete = original.Name,
                    CurrentDocument = new Google.Cloud.Firestore.V1.Precondition { UpdateTime = updateTime }
                }
            }
        };

        if (matches)
        {
            await client.CommitAsync(request);

            Assert.False(client.Documents.ContainsKey(original.Name));
            Assert.Equal(original.Name, Assert.Single(client.CommittedWrites).Delete);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<RpcException>(() => client.CommitAsync(request));

            Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
            Assert.Equal(original, client.Documents[original.Name]);
            Assert.Empty(client.CommittedWrites);
        }

        Assert.Equal(version, client.Documents[VersionPath]);
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
        AssertSingleStrongQuery(client, deadOnly: true);
        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Commits);
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

        AssertSingleStrongQuery(client, deadOnly: true);
        Assert.Equal(0, client.Transactions);
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

        Assert.Equal(1, client.Transactions);
        Assert.Equal(original.Name, Assert.Single(Assert.Single(client.Reads).Documents));
        Assert.False(client.Reads[0].Transaction.IsEmpty);
        var attempted = Assert.Single(client.AttemptedWrites);
        Assert.Equal(original.Name, attempted.Delete);
        Assert.Equal(original.UpdateTime, attempted.CurrentDocument.UpdateTime);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(value, client.Documents[original.Name].Fields[field]);
        Assert.Equal(version, client.Documents[VersionPath]);
        AssertSingleStrongQuery(client, deadOnly: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFiltersDeadRowsBeforeConditionalDelete(bool canonicalChange)
    {
        var client = new MembershipClient();
        var active = client.AddRow(Entry(), DateTime.UnixEpoch);
        var dead = Entry(2);
        dead.Status = SiloStatus.Dead;
        var original = client.AddRow(dead, DateTime.UnixEpoch).Clone();
        Dictionary<string, Document>? concurrentState = null;
        client.BeforeCommit = () =>
        {
            client.Change(active.Name, nameof(SiloInstanceEntity.IAmAliveTime), Time(Now));
            if (canonicalChange)
            {
                client.Change(active.Name, nameof(SiloInstanceEntity.Status), new Value { IntegerValue = (int)SiloStatus.ShuttingDown });
                client.Change(active.Name, nameof(SiloInstanceEntity.MembershipVersion), new Value { IntegerValue = 8 });
                client.Change(VersionPath, nameof(ClusterVersionEntity.MembershipVersion), new Value { IntegerValue = 8 });
            }

            concurrentState = client.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        };

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(0, client.Transactions);
        Assert.Single(client.Queries);
        Assert.Empty(client.Reads);
        Assert.Single(client.AttemptedWrites);
        var write = Assert.Single(client.CommittedWrites);
        Assert.Equal(original.Name, write.Delete);
        Assert.Equal(original.UpdateTime, write.CurrentDocument.UpdateTime);
        Assert.False(client.Documents.ContainsKey(original.Name));
        Assert.NotNull(concurrentState);
        concurrentState.Remove(original.Name);
        Assert.Equal(concurrentState.Count, client.Documents.Count);
        Assert.All(concurrentState, pair => Assert.Equal(pair.Value, client.Documents[pair.Key]));
        AssertSingleStrongQuery(client, deadOnly: true);
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
        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Equal(2, client.Commits.Count);
        Assert.Equal(FirestoreDataManager.MaxBatchSize + 1, client.QueryDocumentReads);
        AssertSingleStrongQuery(client, deadOnly: true);
    }

    [Fact]
    public async Task CleanupRefreshesOnlyTheConflictedBatch()
    {
        var client = new MembershipClient();
        var candidates = Enumerable.Range(1, FirestoreDataManager.MaxBatchSize + 1).Select(generation =>
        {
            var entry = Entry(generation);
            entry.Status = SiloStatus.Dead;
            return client.AddRow(entry, DateTime.UnixEpoch);
        }).ToArray();
        var recentEntry = Entry(1000);
        recentEntry.Status = SiloStatus.Dead;
        var recent = client.AddRow(recentEntry, Now).Clone();
        var live = client.AddRow(Entry(1001), Now).Clone();
        var version = client.Documents[VersionPath].Clone();
        client.BeforeCommit = () => client.Change(candidates[0].Name, nameof(SiloInstanceEntity.IAmAliveTime), Time(Now));

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken);

        AssertSingleStrongQuery(client, deadOnly: true);
        Assert.Equal(FirestoreDataManager.MaxBatchSize + 2, client.QueryDocumentReads);
        Assert.Equal(1, client.Transactions);
        var refresh = Assert.Single(client.Reads);
        Assert.Equal(candidates.Take(FirestoreDataManager.MaxBatchSize).Select(document => document.Name), refresh.Documents);
        Assert.Equal(new[] { 500, 499, 1 }, client.Commits.Select(commit => commit.Writes.Count));
        Assert.Equal(FirestoreDataManager.MaxBatchSize, client.CommittedWrites.Count);
        Assert.Equal(4, client.Documents.Count);
        Assert.Equal(Time(Now), client.Documents[candidates[0].Name].Fields[nameof(SiloInstanceEntity.IAmAliveTime)]);
        Assert.Equal(recent, client.Documents[recent.Name]);
        Assert.Equal(live, client.Documents[live.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Fact]
    public async Task CleanupRefreshesSelectedCandidatesAfterNativeAbort()
    {
        var client = new MembershipClient();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        client.BeforeCommit = () => throw new RpcException(new Status(StatusCode.Aborted, "commit-contention"));

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken);

        AssertSingleStrongQuery(client, deadOnly: true);
        Assert.Equal(1, client.Transactions);
        Assert.Equal(original.Name, Assert.Single(Assert.Single(client.Reads).Documents));
        Assert.Equal(new[] { 1, 1 }, client.Commits.Select(commit => commit.Writes.Count));
        Assert.Equal(original.Name, Assert.Single(client.CommittedWrites).Delete);
        Assert.Equal(version, Assert.Single(client.Documents).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupRefreshHandlesMissingAndStillEligibleCandidates(bool removed)
    {
        var client = new MembershipClient();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        client.BeforeCommit = () =>
        {
            if (removed)
            {
                client.Documents.Remove(original.Name);
            }
            else
            {
                client.Change(original.Name, nameof(SiloInstanceEntity.IAmAliveTime), Time(DateTime.UnixEpoch.AddMinutes(1)));
            }
        };

        await CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken);

        AssertSingleStrongQuery(client, deadOnly: true);
        Assert.Equal(1, client.Transactions);
        Assert.Equal(original.Name, Assert.Single(Assert.Single(client.Reads).Documents));
        Assert.Equal(new[] { 1, removed ? 0 : 1 }, client.Commits.Select(commit => commit.Writes.Count));
        Assert.Equal(version, Assert.Single(client.Documents).Value);
        if (removed)
        {
            Assert.Empty(client.CommittedWrites);
        }
        else
        {
            var deleted = Assert.Single(client.CommittedWrites);
            Assert.Equal(original.Name, deleted.Delete);
            Assert.NotEqual(original.UpdateTime, deleted.CurrentDocument.UpdateTime);
        }
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unavailable)]
    public async Task CleanupPropagatesNativeWriteFailuresWithoutRefreshing(StatusCode status)
    {
        var failure = new RpcException(new Status(status, "cleanup-write-failure"));
        var client = new MembershipClient { CommitFailure = failure };
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Single(client.Commits);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Fact]
    public async Task CleanupPropagatesMissingResourceFailureDuringRefresh()
    {
        var failure = new RpcException(new Status(StatusCode.NotFound, "database-missing"));
        var client = new MembershipClient { CommitFailure = failure };
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        var original = client.AddRow(entry, DateTime.UnixEpoch).Clone();
        var version = client.Documents[VersionPath].Clone();
        client.BeforeCommit = () => client.ReadFailure = failure;

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        AssertSingleStrongQuery(client, deadOnly: true);
        Assert.Equal(1, client.Transactions);
        Assert.Single(client.Reads);
        Assert.Single(client.Commits);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(original, client.Documents[original.Name]);
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Fact]
    public async Task CleanupContentionInOneBatchPreservesOtherBatchFailure()
    {
        var failure = new RpcException(new Status(StatusCode.PermissionDenied, "other-batch-denied"));
        var last = Entry(FirestoreDataManager.MaxBatchSize + 1);
        var client = new MembershipClient
        {
            OnCommit = request =>
            {
                if (request.Writes.Any(write => write.Delete == RowPath(last)))
                {
                    throw failure;
                }
            }
        };
        for (var generation = 1; generation <= FirestoreDataManager.MaxBatchSize + 1; generation++)
        {
            var entry = Entry(generation);
            entry.Status = SiloStatus.Dead;
            client.AddRow(entry, DateTime.UnixEpoch);
        }

        var first = RowPath(Entry());
        var version = client.Documents[VersionPath].Clone();
        client.BeforeCommit = () => client.Change(first, nameof(SiloInstanceEntity.IAmAliveTime), Time(Now));

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => CreateTable(client).CleanupDefunctSiloEntriesAsync(Now, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        AssertSingleStrongQuery(client, deadOnly: true);
        Assert.Equal(1, client.Transactions);
        Assert.Equal(FirestoreDataManager.MaxBatchSize, Assert.Single(client.Reads).Documents.Count);
        Assert.Equal(FirestoreDataManager.MaxBatchSize - 1, client.CommittedWrites.Count);
        Assert.Equal(3, client.Documents.Count);
        Assert.Equal(Time(Now), client.Documents[first].Fields[nameof(SiloInstanceEntity.IAmAliveTime)]);
        Assert.True(client.Documents.ContainsKey(RowPath(last)));
        Assert.Equal(version, client.Documents[VersionPath]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipReadsUseOneStrongNativeSnapshot(bool readAll)
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
        if (readAll)
        {
            Assert.Equal(1, client.Transactions);
            var query = Assert.Single(client.Queries);
            Assert.Equal(RunQueryRequest.ConsistencySelectorOneofCase.Transaction, query.ConsistencySelectorCase);
            Assert.False(query.Transaction.IsEmpty);
            Assert.Equal(2, client.QueryDocumentReads);
            Assert.Empty(client.Reads);
            var commit = Assert.Single(client.Commits);
            Assert.Equal(query.Transaction, commit.Transaction);
            Assert.Empty(commit.Writes);
        }
        else
        {
            Assert.Equal(0, client.Transactions);
            Assert.Empty(client.Commits);
            var read = Assert.Single(client.Reads);
            Assert.Equal(new[] { VersionPath, RowPath(entry) }, read.Documents);
            Assert.Equal(BatchGetDocumentsRequest.ConsistencySelectorOneofCase.None, read.ConsistencySelectorCase);
            Assert.Empty(client.Queries);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipReadsConsumeOneSnapshotDuringStreamedChanges(bool readAll)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var document = client.AddRow(entry, Now);
        document.Fields[nameof(SiloInstanceEntity.ProxyPort)] = new Value { IntegerValue = 7 };
        document.UpdateTime = Timestamp.FromDateTime(DateTime.UnixEpoch.AddSeconds(2));
        var rowETag = ETag(document);
        var token = ETag(client.Documents[VersionPath]);
        var changed = false;
        client.AfterFirstReadResponse = () =>
        {
            if (changed)
            {
                return;
            }

            changed = true;
            client.Change(document.Name, nameof(SiloInstanceEntity.ProxyPort), new Value { IntegerValue = 8 });
            client.Change(document.Name, nameof(SiloInstanceEntity.MembershipVersion), new Value { IntegerValue = 8 });
            client.Change(VersionPath, nameof(ClusterVersionEntity.MembershipVersion), new Value { IntegerValue = 8 });
        };
        var table = CreateTable(client);

        var result = readAll
            ? await table.ReadAllAsync(TestContext.Current.CancellationToken)
            : await table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken);

        var row = Assert.Single(result.Members);
        Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
        if (readAll)
        {
            Assert.Equal(8, result.Version.Version);
            Assert.Equal(ETag(client.Documents[VersionPath]), result.Version.VersionEtag);
            Assert.Equal(8, row.Item1.ProxyPort);
            Assert.Equal(ETag(client.Documents[document.Name]), row.Item2);
            Assert.Equal(2, client.Transactions);
            Assert.Equal(2, client.Queries.Count);
            Assert.Equal(2, client.Commits.Count);
            Assert.Empty(client.Reads);
            Assert.All(client.Queries, query =>
            {
                Assert.Equal(RunQueryRequest.ConsistencySelectorOneofCase.Transaction, query.ConsistencySelectorCase);
                Assert.False(query.Transaction.IsEmpty);
            });
            Assert.Equal(client.Queries.Select(query => query.Transaction), client.Commits.Select(commit => commit.Transaction));
            Assert.All(client.Commits, commit => Assert.Empty(commit.Writes));
        }
        else
        {
            Assert.Equal(7, result.Version.Version);
            Assert.Equal(token, result.Version.VersionEtag);
            Assert.Equal(7, row.Item1.ProxyPort);
            Assert.Equal(rowETag, row.Item2);
            Assert.Equal(0, client.Transactions);
            Assert.Empty(client.Commits);
            Assert.Single(client.Reads);
            Assert.Empty(client.Queries);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipReadsRequireVersionDocument(bool readAll)
    {
        var client = new MembershipClient();
        var entry = Entry();
        client.AddRow(entry, Now);
        client.Documents.Remove(VersionPath);
        var table = CreateTable(client);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => readAll
            ? table.ReadAllAsync(TestContext.Current.CancellationToken)
            : table.ReadRowAsync(entry.SiloAddress, TestContext.Current.CancellationToken));

        Assert.Empty(client.Commits);
        Assert.Equal(1, client.Reads.Count + client.Queries.Count);
        Assert.Equal(readAll ? 1 : 0, client.Transactions);
        if (readAll)
        {
            var query = Assert.Single(client.Queries);
            Assert.Equal(RunQueryRequest.ConsistencySelectorOneofCase.Transaction, query.ConsistencySelectorCase);
        }
    }

    [Fact]
    public async Task ReadRowReturnsVersionForMissingSiloInOneRequest()
    {
        var client = new MembershipClient();
        var version = client.Documents[VersionPath].Clone();

        var result = await CreateTable(client).ReadRowAsync(Entry().SiloAddress, TestContext.Current.CancellationToken);

        Assert.Empty(result.Members);
        Assert.Equal(7, result.Version.Version);
        Assert.Equal(ETag(version), result.Version.VersionEtag);
        Assert.Equal(2, Assert.Single(client.Reads).Documents.Count);
        Assert.Empty(client.Queries);
        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Commits);
    }

    [Theory]
    [InlineData(false, StatusCode.PermissionDenied)]
    [InlineData(true, StatusCode.PermissionDenied)]
    [InlineData(false, StatusCode.NotFound)]
    [InlineData(true, StatusCode.NotFound)]
    public async Task MembershipReadsPropagateNativeFailures(bool readAll, StatusCode status)
    {
        var failure = new RpcException(new Status(status, "native-read-failure"));
        var client = new MembershipClient { ReadFailure = failure };
        var table = CreateTable(client);

        var exception = await Assert.ThrowsAsync<RpcException>(() => readAll
            ? table.ReadAllAsync(TestContext.Current.CancellationToken)
            : table.ReadRowAsync(Entry().SiloAddress, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(readAll ? 1 : 0, client.Transactions);
        Assert.Empty(client.Commits);
        Assert.Equal(1, client.Reads.Count + client.Queries.Count);
    }

    [Theory]
    [InlineData("ReadRow")]
    [InlineData("ReadAll")]
    [InlineData("InsertRow")]
    [InlineData("UpdateRow")]
    [InlineData("Cleanup")]
    public async Task NativeMembershipOperationsNormalizeCallerCancellation(string operation)
    {
        var failure = new RpcException(new Status(StatusCode.Cancelled, "native-cancellation"));
        var reads = operation is "ReadRow" or "ReadAll" or "Cleanup";
        var client = new MembershipClient { ReadFailure = reads ? failure : null, CommitFailure = reads ? null : failure };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        client.BeforeRead = cancellation.Cancel;
        client.BeforeCommit = cancellation.Cancel;
        var table = CreateTable(client);
        var entry = Entry();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(operation == "ReadAll" ? 1 : 0, client.Transactions);
        Assert.Equal(1, client.Reads.Count + client.Queries.Count + client.Commits.Count);
        Assert.Empty(client.CommittedWrites);

        Task Execute() => operation switch
        {
            "ReadRow" => table.ReadRowAsync(entry.SiloAddress, cancellation.Token),
            "ReadAll" => table.ReadAllAsync(cancellation.Token),
            "InsertRow" => table.InsertRowAsync(entry, NextVersion(client), cancellation.Token),
            "UpdateRow" => table.UpdateRowAsync(entry, ETag(client.Documents[VersionPath]), NextVersion(client), cancellation.Token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(Now, cancellation.Token),
            _ => throw new InvalidOperationException(operation)
        };
    }

    [Fact]
    public async Task InsertAtomicallyCreatesRowAndAdvancesVersion()
    {
        var client = new MembershipClient();
        var entry = Entry();

        Assert.True(await CreateTable(client).InsertRowAsync(entry, NextVersion(client), TestContext.Current.CancellationToken));

        var commit = Assert.Single(client.Commits);
        Assert.Equal(0, client.Transactions);
        Assert.True(commit.Transaction.IsEmpty);
        Assert.Empty(client.Reads);
        Assert.Equal(2, commit.Writes.Count);
        Assert.Equal(RowPath(entry), commit.Writes[0].Update.Name);
        Assert.False(commit.Writes[0].CurrentDocument.Exists);
        Assert.Equal(VersionPath, commit.Writes[1].Update.Name);
        Assert.Equal(Timestamp.FromDateTime(DateTime.UnixEpoch), commit.Writes[1].CurrentDocument.UpdateTime);
        Assert.Equal(8, client.Documents[VersionPath].Fields[nameof(ClusterVersionEntity.MembershipVersion)].IntegerValue);
        Assert.Equal(8, client.Documents[RowPath(entry)].Fields[nameof(SiloInstanceEntity.MembershipVersion)].IntegerValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertConflictsHaveNoPartialEffects(bool duplicateRow)
    {
        var client = new MembershipClient();
        var entry = Entry();
        var version = NextVersion(client);
        if (duplicateRow)
        {
            client.AddRow(entry, Now);
        }
        else
        {
            client.Change(VersionPath, nameof(ClusterVersionEntity.MembershipVersion), new Value { IntegerValue = 8 });
        }

        var expected = client.Documents.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());

        Assert.False(await CreateTable(client).InsertRowAsync(entry, version, TestContext.Current.CancellationToken));

        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        Assert.Single(client.Commits);
        Assert.Empty(client.CommittedWrites);
        Assert.Equal(expected.Count, client.Documents.Count);
        Assert.All(expected, pair => Assert.Equal(pair.Value, client.Documents[pair.Key]));
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

    private static void AssertSingleHeartbeatWrite(MembershipClient client, MembershipEntry entry)
    {
        Assert.Equal(0, client.Transactions);
        Assert.Empty(client.Reads);
        Assert.Empty(client.Queries);
        var commit = Assert.Single(client.Commits);
        Assert.True(commit.Transaction.IsEmpty);
        var write = Assert.Single(commit.Writes);
        Assert.Single(client.AttemptedWrites);
        Assert.Equal(Write.OperationOneofCase.Update, write.OperationCase);
        Assert.Equal(RowPath(entry), write.Update.Name);
        Assert.Equal(nameof(SiloInstanceEntity.IAmAliveTime), Assert.Single(write.UpdateMask.FieldPaths));
        var field = Assert.Single(write.Update.Fields);
        Assert.Equal(nameof(SiloInstanceEntity.IAmAliveTime), field.Key);
        Assert.Equal(Time(entry.IAmAliveTime), field.Value);
        Assert.Empty(write.UpdateTransforms);
        Assert.Equal(Google.Cloud.Firestore.V1.Precondition.ConditionTypeOneofCase.Exists, write.CurrentDocument.ConditionTypeCase);
        Assert.True(write.CurrentDocument.Exists);
    }

    private static void AssertSingleStrongQuery(MembershipClient client, bool deadOnly = false)
    {
        Assert.Single(client.Queries);
        Assert.All(client.Queries, query =>
        {
            Assert.Equal(RunQueryRequest.ConsistencySelectorOneofCase.None, query.ConsistencySelectorCase);
            Assert.Equal(CollectionPath, $"{query.Parent}/{Assert.Single(query.StructuredQuery.From).CollectionId}");
            if (deadOnly)
            {
                var filter = query.StructuredQuery.Where;
                Assert.NotNull(filter);
                Assert.Equal(StructuredQuery.Types.Filter.FilterTypeOneofCase.FieldFilter, filter.FilterTypeCase);
                Assert.Equal(StructuredQuery.Types.FieldFilter.Types.Operator.Equal, filter.FieldFilter.Op);
                Assert.Equal(nameof(SiloInstanceEntity.Status), filter.FieldFilter.Field.FieldPath);
                Assert.Equal(new Value { IntegerValue = (int)SiloStatus.Dead }, filter.FieldFilter.Value);
            }
            else
            {
                Assert.Null(query.StructuredQuery.Where);
            }
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
        public Action<CommitRequest>? OnCommit { get; init; }
        public Action? BeforeRead { get; set; }
        public Action? AfterFirstReadResponse { get; set; }
        public RpcException? ReadFailure { get; set; }
        public RpcException? CommitFailure { get; init; }
        public CancellationToken? CommitCancellationToken { get; private set; }
        public int Transactions { get; private set; }
        public int QueryDocumentReads { get; private set; }
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
            BeforeRead?.Invoke();
            if (ReadFailure is { } failure)
            {
                throw failure;
            }

            var documents = Documents;
            if (!request.Transaction.IsEmpty)
            {
                _readSets[request.Transaction].UnionWith(request.Documents);
                documents = _snapshots[request.Transaction];
            }

            var responses = request.Documents.Reverse().Select(name =>
            {
                var response = new BatchGetDocumentsResponse { ReadTime = Timestamp.FromDateTime(Now) };
                if (documents.TryGetValue(name, out var document))
                {
                    response.Found = document.Clone();
                }
                else
                {
                    response.Missing = name;
                }

                return response;
            }).ToArray();

            return new DocumentStream(responses, AfterFirstReadResponse);
        }

        public override RunQueryStream RunQuery(RunQueryRequest request, CallSettings? callSettings = null)
        {
            Queries.Add(request.Clone());
            BeforeRead?.Invoke();
            if (ReadFailure is { } failure)
            {
                throw failure;
            }

            var collection = $"{request.Parent}/{Assert.Single(request.StructuredQuery.From).CollectionId}/";
            var documents = request.Transaction.IsEmpty ? Documents : _snapshots[request.Transaction];
            var matches = documents.Values.Where(document => document.Name.StartsWith(collection, StringComparison.Ordinal));
            if (request.StructuredQuery.Where is { } filter)
            {
                Assert.Equal(StructuredQuery.Types.Filter.FilterTypeOneofCase.FieldFilter, filter.FilterTypeCase);
                Assert.Equal(StructuredQuery.Types.FieldFilter.Types.Operator.Equal, filter.FieldFilter.Op);
                matches = matches.Where(document =>
                    document.Fields.TryGetValue(filter.FieldFilter.Field.FieldPath, out var value)
                    && value.Equals(filter.FieldFilter.Value));
            }

            var responses = matches.Select(document =>
                {
                    if (!request.Transaction.IsEmpty)
                    {
                        _readSets[request.Transaction].Add(document.Name);
                    }

                    return new RunQueryResponse { Document = document.Clone(), ReadTime = Timestamp.FromDateTime(Now) };
                }).ToArray();
            QueryDocumentReads += responses.Length;
            return new QueryStream(
                responses.Length > 0 ? responses : [new RunQueryResponse { ReadTime = Timestamp.FromDateTime(Now) }],
                AfterFirstReadResponse);
        }

        public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? callSettings = null)
        {
            Commits.Add(request.Clone());
            AttemptedWrites.AddRange(request.Writes.Select(write => write.Clone()));
            CommitCancellationToken = callSettings?.CancellationToken;
            var beforeCommit = BeforeCommit;
            BeforeCommit = null;
            beforeCommit?.Invoke();
            OnCommit?.Invoke(request);
            if (CommitFailure is { } failure)
            {
                return Task.FromException<CommitResponse>(failure);
            }

            CommitCancellationToken?.ThrowIfCancellationRequested();
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
                        ? !Equals(current?.UpdateTime, precondition.UpdateTime)
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

    private sealed class DocumentStream(IEnumerable<BatchGetDocumentsResponse> responses, Action? afterFirstResponse) : FirestoreClient.BatchGetDocumentsStream
    {
        public override AsyncServerStreamingCall<BatchGetDocumentsResponse> GrpcCall { get; } = CreateStream(responses, afterFirstResponse);
    }

    private sealed class QueryStream(IEnumerable<RunQueryResponse> responses, Action? afterFirstResponse) : FirestoreClient.RunQueryStream
    {
        public override AsyncServerStreamingCall<RunQueryResponse> GrpcCall { get; } = CreateStream(responses, afterFirstResponse);
    }

    private static AsyncServerStreamingCall<T> CreateStream<T>(IEnumerable<T> responses, Action? afterFirstResponse) => new(
        new ResponseReader<T>(responses, afterFirstResponse), Task.FromResult(new global::Grpc.Core.Metadata()), () => Status.DefaultSuccess, () => new global::Grpc.Core.Metadata(), () => { });

    private sealed class ResponseReader<T>(IEnumerable<T> responses, Action? afterFirstResponse) : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _responses = responses.GetEnumerator();
        private int _reads;
        public T Current => _responses.Current;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_reads++ == 1)
            {
                afterFirstResponse?.Invoke();
            }

            return Task.FromResult(_responses.MoveNext());
        }
    }
}
