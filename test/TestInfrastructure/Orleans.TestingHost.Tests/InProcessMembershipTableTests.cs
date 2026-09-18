using Orleans.Runtime;
using Orleans.TestingHost.InProcess;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
[TestCategory("BVT"), TestCategory("Membership")]
public sealed class InProcessMembershipTableTests
{
    private readonly IMembershipTable _table = new InProcessMembershipTable("cluster");
    private readonly CancellationToken _cancellationToken = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writes_CopyMutableEntryAndSuspectTimes(bool update)
    {
        var entry = CreateEntry(SiloStatus.Joining);
        var suspector = SiloAddress.FromParsableString("127.0.0.1:20000@1");
        entry.AddSuspector(suspector, DateTime.UnixEpoch);
        await Insert(entry);
        if (update)
        {
            var current = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
            entry.Status = SiloStatus.Active;
            Assert.True(await _table.UpdateRowAsync(entry, Assert.Single(current.Members).Item2, current.Version.Next(), _cancellationToken));
        }

        var expectedStatus = entry.Status;
        var expectedHeartbeat = entry.IAmAliveTime;
        var before = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        var etag = Assert.Single(before.Members).Item2;
        entry.Status = SiloStatus.Dead;
        entry.IAmAliveTime = expectedHeartbeat.AddDays(1);
        entry.SuspectTimes!.Clear();

        var after = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        var stored = Assert.Single(after.Members);
        Assert.NotSame(entry, stored.Item1);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(etag, stored.Item2);
        Assert.Equal(expectedStatus, stored.Item1.Status);
        Assert.Equal(expectedHeartbeat, stored.Item1.IAmAliveTime);
        Assert.Equal(Tuple.Create(suspector, DateTime.UnixEpoch), Assert.Single(stored.Item1.SuspectTimes!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reads_ReturnIndependentMutableSnapshots(bool readAll)
    {
        var entry = CreateEntry(SiloStatus.Joining);
        var suspector = SiloAddress.FromParsableString("127.0.0.1:20000@1");
        entry.AddSuspector(suspector, DateTime.UnixEpoch);
        await Insert(entry);
        var snapshot = readAll
            ? await _table.ReadAllAsync(_cancellationToken)
            : await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        var snapshotRow = Assert.Single(snapshot.Members);
        var heartbeat = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            IAmAliveTime = entry.IAmAliveTime.AddHours(1)
        };
        await _table.UpdateIAmAliveAsync(heartbeat, _cancellationToken);

        Assert.Equal(entry.IAmAliveTime, snapshotRow.Item1.IAmAliveTime);
        snapshotRow.Item1.Status = SiloStatus.Dead;
        snapshotRow.Item1.IAmAliveTime = DateTime.UnixEpoch;
        snapshotRow.Item1.SuspectTimes!.Clear();

        var after = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        var stored = Assert.Single(after.Members).Item1;
        Assert.Equal(snapshot.Version, after.Version);
        Assert.Equal(SiloStatus.Joining, stored.Status);
        Assert.Equal(heartbeat.IAmAliveTime, stored.IAmAliveTime);
        Assert.Equal(Tuple.Create(suspector, DateTime.UnixEpoch), Assert.Single(stored.SuspectTimes!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task UpdateIAmAlive_OwnerReport_ChangesOnlyTimestampAndRowEtag(int clockOffsetMinutes)
    {
        var entry = CreateEntry(SiloStatus.Active);
        entry.IAmAliveTime = entry.StartTime.AddHours(1);
        entry.RoleName = "worker";
        entry.UpdateZone = 3;
        entry.FaultZone = 5;
        entry.ProxyPort = 20000;
        entry.AddSuspector(SiloAddress.FromParsableString("127.0.0.1:20001@1"), DateTime.UnixEpoch);
        await Insert(entry);
        var before = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        // The owning silo can report the same time or a clock adjustment.
        var heartbeat = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            IAmAliveTime = entry.IAmAliveTime.AddMinutes(clockOffsetMinutes)
        };

        await _table.UpdateIAmAliveAsync(heartbeat, _cancellationToken);

        var after = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        var stored = Assert.Single(after.Members);
        Assert.Equal(before.Version, after.Version);
        Assert.NotEqual(Assert.Single(before.Members).Item2, stored.Item2);
        Assert.Equal(heartbeat.IAmAliveTime, stored.Item1.IAmAliveTime);
        Assert.Equal(entry.SiloAddress, stored.Item1.SiloAddress);
        Assert.Equal(entry.Status, stored.Item1.Status);
        Assert.Equal(entry.StartTime, stored.Item1.StartTime);
        Assert.Equal(entry.SiloName, stored.Item1.SiloName);
        Assert.Equal(entry.HostName, stored.Item1.HostName);
        Assert.Equal(entry.RoleName, stored.Item1.RoleName);
        Assert.Equal(entry.UpdateZone, stored.Item1.UpdateZone);
        Assert.Equal(entry.FaultZone, stored.Item1.FaultZone);
        Assert.Equal(entry.ProxyPort, stored.Item1.ProxyPort);
        Assert.Equal(entry.SuspectTimes, stored.Item1.SuspectTimes);
        Assert.Equal(entry.IAmAliveTime, Assert.Single(before.Members).Item1.IAmAliveTime);
    }

    [Fact]
    public async Task Update_WithCurrentTokens_PreservesMaximumHeartbeat()
    {
        var entry = CreateEntry(SiloStatus.Joining);
        var originalHeartbeat = entry.IAmAliveTime;
        await Insert(entry);
        var initial = await _table.ReadAllAsync(_cancellationToken);
        var heartbeat = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            IAmAliveTime = entry.IAmAliveTime.AddHours(2)
        };
        var maximum = heartbeat.IAmAliveTime;
        await _table.UpdateIAmAliveAsync(heartbeat, _cancellationToken);
        var beforeUpdate = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        Assert.Equal(initial.Version, beforeUpdate.Version);
        Assert.Equal(maximum, Assert.Single(beforeUpdate.Members).Item1.IAmAliveTime);

        entry.Status = SiloStatus.Active;
        Assert.True(await _table.UpdateRowAsync(entry, Assert.Single(beforeUpdate.Members).Item2, beforeUpdate.Version.Next(), _cancellationToken));

        var after = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        var stored = Assert.Single(after.Members).Item1;
        Assert.Equal(beforeUpdate.Version.Version + 1, after.Version.Version);
        Assert.Equal(SiloStatus.Active, stored.Status);
        Assert.Equal(maximum, stored.IAmAliveTime);
        Assert.Equal(originalHeartbeat, entry.IAmAliveTime);
    }

    [Fact]
    public async Task CleanupDefunctSiloEntries_PreservesEveryNonDeadStatus()
    {
        foreach (var status in Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.None))
        {
            var entry = CreateEntry(status, port: 10000 + (int)status);
            await Insert(entry);
        }

        var before = await _table.ReadAllAsync(_cancellationToken);
        await _table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(5), _cancellationToken);

        var after = await _table.ReadAllAsync(_cancellationToken);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(
            Enum.GetValues<SiloStatus>().Where(status => status is not SiloStatus.None and not SiloStatus.Dead).Order(),
            after.Members.Select(row => row.Item1.Status).Order());
        await _table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(5), _cancellationToken);
        var repeated = await _table.ReadAllAsync(_cancellationToken);
        Assert.Equal(after.Version, repeated.Version);
        Assert.Equal(after.Members.Select(row => row.Item2), repeated.Members.Select(row => row.Item2));
    }

    [Fact]
    public async Task CleanupDefunctSiloEntries_PreservesRecentlyDeclaredDeadWithStaleHeartbeat()
    {
        var entry = CreateEntry(SiloStatus.Active);
        await Insert(entry);
        var beforeUpdate = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        entry.Status = SiloStatus.Dead;
        entry.AddSuspector(SiloAddress.FromParsableString("127.0.0.1:20000@1"), DateTime.UnixEpoch.AddDays(10));
        Assert.True(await _table.UpdateRowAsync(entry, Assert.Single(beforeUpdate.Members).Item2, beforeUpdate.Version.Next(), _cancellationToken));
        var beforeCleanup = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);

        await _table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(5), _cancellationToken);

        var after = await _table.ReadRowAsync(entry.SiloAddress, _cancellationToken);
        var stored = Assert.Single(after.Members);
        Assert.Equal(beforeCleanup.Version, after.Version);
        Assert.Equal(Assert.Single(beforeCleanup.Members).Item2, stored.Item2);
        Assert.Equal(SiloStatus.Dead, stored.Item1.Status);
        Assert.Equal(DateTime.UnixEpoch, stored.Item1.IAmAliveTime);
        Assert.Equal(DateTime.UnixEpoch.AddDays(10), Assert.Single(stored.Item1.SuspectTimes!).Item2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupDefunctSiloEntries_PreservesVersionIncludingAtLimit(bool atVersionLimit)
    {
        var entries = new[]
        {
            CreateEntry(SiloStatus.Dead, 11001),
            CreateEntry(SiloStatus.Dead, 11002),
            CreateEntry(SiloStatus.Active, 11003)
        };
        foreach (var entry in entries)
        {
            var current = (await _table.ReadAllAsync(_cancellationToken)).Version;
            // Seed the version limit directly to exercise unversioned cleanup at the boundary.
            var candidate = atVersionLimit && entry == entries[^1]
                ? new TableVersion(int.MaxValue, current.VersionEtag)
                : current.Next();
            Assert.True(await _table.InsertRowAsync(entry, candidate, _cancellationToken));
        }
        var before = await _table.ReadAllAsync(_cancellationToken);
        var cutoff = DateTimeOffset.UnixEpoch.AddDays(1);

        await _table.CleanupDefunctSiloEntriesAsync(cutoff, _cancellationToken);
        var after = await _table.ReadAllAsync(_cancellationToken);
        Assert.Equal(before.Version, after.Version);
        var survivor = Assert.Single(after.Members);
        Assert.Equal(entries[2].ToFullString(), survivor.Item1.ToFullString());
        Assert.Equal(before.TryGet(entries[2].SiloAddress)!.Item2, survivor.Item2);
        await _table.CleanupDefunctSiloEntriesAsync(cutoff, _cancellationToken);
        Assert.Equal(after.Version, (await _table.ReadAllAsync(_cancellationToken)).Version);
    }

    private async Task Insert(MembershipEntry entry)
    {
        var current = await _table.ReadAllAsync(_cancellationToken);
        Assert.True(await _table.InsertRowAsync(entry, current.Version.Next(), _cancellationToken));
    }

    private static MembershipEntry CreateEntry(SiloStatus status, int port = 10000) => new()
    {
        SiloAddress = SiloAddress.FromParsableString($"127.0.0.1:{port}@1"),
        HostName = "localhost",
        SiloName = $"Silo-{port}",
        Status = status,
        StartTime = DateTime.UnixEpoch,
        IAmAliveTime = DateTime.UnixEpoch
    };
}
