using System.Net;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Dissemination.PerformanceHarness;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public sealed class FileMembershipTableTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"orleans-dissemination-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public async Task PersistedSuspectVotesPreserveDuplicatesOrderAndLatestUpdate()
    {
        var table = CreateTable();
        var cancellationToken = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, cancellationToken);
        var entry = CreateEntry(SiloStatus.Dead, 1);
        var repeatedVoter = CreateEntry(SiloStatus.Active, 2).SiloAddress;
        entry.AddSuspector(repeatedVoter, Cutoff.AddSeconds(-1));
        entry.AddSuspector(repeatedVoter, Cutoff);
        entry.AddSuspector(CreateEntry(SiloStatus.Active, 3).SiloAddress, Cutoff.AddSeconds(-2));
        var initial = await table.ReadAllAsync(cancellationToken);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), cancellationToken));

        var reopened = CreateTable();
        var inserted = await reopened.ReadAllAsync(cancellationToken);
        var insertedRow = Assert.Single(inserted.Members);
        Assert.Equal(entry.SuspectTimes, insertedRow.Item1.SuspectTimes);
        Assert.Equal(Cutoff, insertedRow.Item1.EffectiveUpdateTime);

        entry.AddSuspector(repeatedVoter, Cutoff.AddSeconds(1));
        Assert.True(await reopened.UpdateRowAsync(entry, insertedRow.Item2, inserted.Version.Next(), cancellationToken));
        var updatedTable = CreateTable();
        var updated = await updatedTable.ReadAllAsync(cancellationToken);
        var updatedRow = Assert.Single(updated.Members);
        Assert.Equal(entry.SuspectTimes, updatedRow.Item1.SuspectTimes);
        Assert.Equal(Cutoff.AddSeconds(1), updatedRow.Item1.EffectiveUpdateTime);

        await updatedTable.CleanupDefunctSiloEntriesAsync(Cutoff.AddSeconds(1), cancellationToken);
        var retained = Assert.Single((await updatedTable.ReadAllAsync(cancellationToken)).Members);
        Assert.Equal(entry.SuspectTimes, retained.Item1.SuspectTimes);
        await updatedTable.CleanupDefunctSiloEntriesAsync(Cutoff.AddSeconds(1).AddTicks(1), cancellationToken);
        Assert.Empty((await updatedTable.ReadAllAsync(cancellationToken)).Members);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistedSuspectVotesPreserveNullAndEmptyLists(bool absent)
    {
        var table = CreateTable();
        var cancellationToken = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, cancellationToken);
        var entry = CreateEntry(SiloStatus.Active, 1);
        entry.SuspectTimes = absent ? null : [];
        var initial = await table.ReadAllAsync(cancellationToken);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), cancellationToken));

        var reopened = CreateTable();
        var persisted = Assert.Single((await reopened.ReadAllAsync(cancellationToken)).Members).Item1.SuspectTimes;
        if (absent)
        {
            Assert.Null(persisted);
        }
        else
        {
            Assert.NotNull(persisted);
            Assert.Empty(persisted);
        }
    }

    [Fact]
    public async Task DeleteInvalidatesThePreviousTableEtag()
    {
        var table = CreateTable();
        var cancellationToken = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, cancellationToken);
        var entry = CreateEntry(SiloStatus.Active, 1);
        var initial = await table.ReadAllAsync(cancellationToken);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), cancellationToken));
        var before = await table.ReadAllAsync(cancellationToken);
        var previousRow = Assert.Single(before.Members);

        await table.DeleteMembershipTableEntriesAsync("cleanup", cancellationToken);
        var reopened = CreateTable();
        var deleted = await reopened.ReadAllAsync(cancellationToken);
        Assert.Empty(deleted.Members);
        Assert.Equal(before.Version.Version + 1, deleted.Version.Version);
        Assert.NotEqual(before.Version.VersionEtag, deleted.Version.VersionEtag);
        Assert.False(await reopened.InsertRowAsync(entry, before.Version.Next(), cancellationToken));
        Assert.False(await reopened.UpdateRowAsync(entry, previousRow.Item2, before.Version.Next(), cancellationToken));
        var afterRejectedWrites = await reopened.ReadAllAsync(cancellationToken);
        Assert.Empty(afterRejectedWrites.Members);
        Assert.Equal(deleted.Version, afterRejectedWrites.Version);

        Assert.True(await reopened.InsertRowAsync(entry, deleted.Version.Next(), cancellationToken));
        var inserted = await reopened.ReadAllAsync(cancellationToken);
        var currentRow = Assert.Single(inserted.Members);
        Assert.Equal(entry.SiloAddress, currentRow.Item1.SiloAddress);
        Assert.False(await reopened.UpdateRowAsync(entry, currentRow.Item2, before.Version.Next(), cancellationToken));
        Assert.Equal(inserted.Version, (await reopened.ReadAllAsync(cancellationToken)).Version);
    }

    [Fact]
    public async Task HeartbeatPreservesTableVersionAndOtherColumns()
    {
        var table = CreateTable();
        var cancellationToken = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, cancellationToken);
        var entry = CreateEntry(SiloStatus.Active, 1);
        var initial = await table.ReadAllAsync(cancellationToken);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), cancellationToken));
        var before = await table.ReadAllAsync(cancellationToken);
        var previousRow = Assert.Single(before.Members);

        var update = CreateEntry(SiloStatus.Dead, 1);
        update.HostName = "ignored";
        update.IAmAliveTime = Cutoff;
        await table.UpdateIAmAliveAsync(update, cancellationToken);
        var reopened = CreateTable();
        var after = await reopened.ReadAllAsync(cancellationToken);
        var currentRow = Assert.Single(after.Members);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(previousRow.Item2, currentRow.Item2);
        var expected = FileMembershipTable.EntryData.From(previousRow.Item1) with { IAmAliveTime = Cutoff };
        Assert.Equal(StateComparison.Serialize(expected), StateComparison.Serialize(FileMembershipTable.EntryData.From(currentRow.Item1)));
    }

    [Fact]
    public async Task CleanupRemovesEveryExpiredNonActiveStatus()
    {
        var table = CreateTable();
        await table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken);
        foreach (var status in Enum.GetValues<SiloStatus>())
        {
            var entry = CreateEntry(status, (int)status + 1);
            var data = await table.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.True(await table.InsertRowAsync(entry, data.Version.Next(), TestContext.Current.CancellationToken));
        }

        var before = await table.ReadAllAsync(TestContext.Current.CancellationToken);
        var expected = Assert.Single(before.Members, row => row.Item1.Status == SiloStatus.Active);
        var reopened = CreateTable();
        await reopened.CleanupDefunctSiloEntriesAsync(Cutoff, TestContext.Current.CancellationToken);
        var after = await reopened.ReadAllAsync(TestContext.Current.CancellationToken);

        var remaining = Assert.Single(after.Members);
        Assert.Equal(expected.Item1.SiloAddress, remaining.Item1.SiloAddress);
        Assert.Equal(SiloStatus.Active, remaining.Item1.Status);
        Assert.Equal(expected.Item2, remaining.Item2);
        Assert.Equal(before.Version.Version, after.Version.Version);
        Assert.Equal(before.Version.VersionEtag, after.Version.VersionEtag);
    }

    [Theory]
    [InlineData("start", -1)]
    [InlineData("start", 0)]
    [InlineData("start", 1)]
    [InlineData("heartbeat", -1)]
    [InlineData("heartbeat", 0)]
    [InlineData("heartbeat", 1)]
    [InlineData("suspect", -1)]
    [InlineData("suspect", 0)]
    [InlineData("suspect", 1)]
    public async Task CleanupUsesEffectiveUpdateTimeAndExclusiveCutoff(string timestamp, int offset)
    {
        var table = CreateTable();
        await table.InitializeMembershipTableAsync(true, TestContext.Current.CancellationToken);
        var entry = CreateEntry(SiloStatus.Dead, 1);
        var latest = Cutoff.AddTicks(offset);
        switch (timestamp)
        {
            case "start":
                entry.StartTime = latest;
                break;
            case "heartbeat":
                entry.IAmAliveTime = latest;
                break;
            case "suspect":
                entry.SuspectTimes =
                [
                    Tuple.Create(CreateEntry(SiloStatus.Active, 2).SiloAddress, latest),
                    Tuple.Create(CreateEntry(SiloStatus.Active, 3).SiloAddress, Cutoff.AddDays(-2)),
                ];
                break;
        }

        var data = await table.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.True(await table.InsertRowAsync(entry, data.Version.Next(), TestContext.Current.CancellationToken));
        var reopened = CreateTable();
        var persisted = Assert.Single((await reopened.ReadAllAsync(TestContext.Current.CancellationToken)).Members);
        Assert.Equal(latest, persisted.Item1.EffectiveUpdateTime);

        await reopened.CleanupDefunctSiloEntriesAsync(Cutoff, TestContext.Current.CancellationToken);
        var after = await reopened.ReadAllAsync(TestContext.Current.CancellationToken);
        if (offset < 0)
        {
            Assert.Empty(after.Members);
        }
        else
        {
            var remaining = Assert.Single(after.Members);
            Assert.Equal(entry.SiloAddress, remaining.Item1.SiloAddress);
            Assert.Equal(latest, remaining.Item1.EffectiveUpdateTime);
            Assert.Equal(persisted.Item2, remaining.Item2);
        }
    }

    private FileMembershipTable CreateTable() =>
        new(new("cleanup", "cleanup", _directory, 0, Environment.ProcessId, Enabled: false));

    private static MembershipEntry CreateEntry(SiloStatus status, int port) => new()
    {
        SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 10000 + port), 1),
        HostName = "localhost",
        SiloName = $"cleanup-{port}",
        Status = status,
        StartTime = Cutoff.AddDays(-1),
        IAmAliveTime = Cutoff.AddDays(-1),
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
