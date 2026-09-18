using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.MembershipTests;

public partial class InMemoryMembershipTableTests
{
    private readonly ServiceProvider _services;

    public void Dispose() => _services.Dispose();

    [Fact]
    public void Insert_WithNextVersion_CommitsExactRowAndVersion()
    {
        var entry = CreateConformanceEntry(1);
        var before = table.ReadTableVersion();

        Assert.True(table.Insert(entry, before.Next()));

        var after = table.Read(entry.SiloAddress);
        var stored = Assert.Single(after.Members);
        Assert.Equal(before.Version + 1, after.Version.Version);
        Assert.NotEqual(before.VersionEtag, after.Version.VersionEtag);
        Assert.NotEmpty(stored.Item2);
        Assert.NotSame(entry, stored.Item1);
        AssertConformanceEntry(entry, stored.Item1);
    }

    [Fact]
    public void Update_WithPreHeartbeatTokens_CommitsCanonicalFields()
    {
        var entry = CreateConformanceEntry(1);
        Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
        var before = table.Read(entry.SiloAddress);
        var heartbeat = entry.Copy();
        heartbeat.IAmAliveTime = entry.IAmAliveTime.AddMinutes(2);
        table.UpdateIAmAlive(heartbeat);
        entry.Status = SiloStatus.Active;
        entry.HostName = "updated-host";
        var inputHeartbeat = entry.IAmAliveTime;

        Assert.True(table.Update(entry, Assert.Single(before.Members).Item2, before.Version.Next()));

        var after = table.Read(entry.SiloAddress);
        var row = Assert.Single(after.Members);
        AssertCanonicalFields(entry, row.Item1);
        Assert.Equal(inputHeartbeat, entry.IAmAliveTime);
        Assert.Equal(before.Version.Version + 1, after.Version.Version);
        Assert.NotEqual(before.Version.VersionEtag, after.Version.VersionEtag);
        Assert.NotEqual(Assert.Single(before.Members).Item2, row.Item2);
    }

    [Fact]
    public void ConditionalWrites_StaleTokens_DoNotChangeStoredState()
    {
        var entry = CreateConformanceEntry(1);
        var initial = table.ReadTableVersion();
        Assert.True(table.Insert(entry, initial.Next()));
        var first = table.Read(entry.SiloAddress);
        var staleRowToken = Assert.Single(first.Members).Item2;
        entry.Status = SiloStatus.Active;
        Assert.True(table.Update(entry, staleRowToken, first.Version.Next()));
        var before = table.ReadAll();
        var currentRow = Assert.Single(before.Members);
        entry.Status = SiloStatus.ShuttingDown;

        Assert.False(table.Insert(CreateConformanceEntry(2), initial.Next()));
        Assert.False(table.Update(entry, currentRow.Item2, first.Version.Next()));
        Assert.False(table.Update(entry, staleRowToken, before.Version.Next()));

        var after = table.ReadAll();
        var stored = Assert.Single(after.Members);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(currentRow.Item2, stored.Item2);
        AssertConformanceEntry(currentRow.Item1, stored.Item1);
        Assert.Empty(table.Read(CreateConformanceEntry(2).SiloAddress).Members);
    }

    [Fact]
    public void ReadAndReadAll_ReturnDetachedEquivalentEntries()
    {
        var entry = CreateConformanceEntry(1);
        Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
        var point = table.Read(entry.SiloAddress);
        var all = table.ReadAll();
        var pointRow = Assert.Single(point.Members);
        var allRow = Assert.Single(all.Members);
        Assert.Equal(point.Version, all.Version);
        Assert.Equal(pointRow.Item2, allRow.Item2);
        Assert.NotSame(pointRow.Item1, allRow.Item1);
        Assert.NotSame(pointRow.Item1.SuspectTimes, allRow.Item1.SuspectTimes);
        AssertConformanceEntry(entry, pointRow.Item1);
        AssertConformanceEntry(entry, allRow.Item1);

        pointRow.Item1.HostName = "caller-only";
        pointRow.Item1.SuspectTimes!.Clear();
        allRow.Item1.IAmAliveTime = entry.IAmAliveTime.AddDays(1);
        allRow.Item1.SuspectTimes!.Clear();

        var after = table.Read(entry.SiloAddress);
        Assert.Equal(point.Version, after.Version);
        Assert.Equal(pointRow.Item2, Assert.Single(after.Members).Item2);
        AssertConformanceEntry(entry, Assert.Single(after.Members).Item1);
        var absent = table.Read(CreateConformanceEntry(2).SiloAddress);
        Assert.Empty(absent.Members);
        Assert.Equal(point.Version, absent.Version);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void UpdateIAmAlive_OwnerReport_ChangesOnlyTimestamp(int clockOffsetMinutes)
    {
        var entry = CreateConformanceEntry(1);
        Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
        var before = table.Read(entry.SiloAddress);
        // The owning silo can report the same time or a clock adjustment.
        var heartbeat = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            IAmAliveTime = entry.IAmAliveTime.AddMinutes(clockOffsetMinutes)
        };

        table.UpdateIAmAlive(heartbeat);

        var after = table.Read(entry.SiloAddress);
        var expected = entry.Copy();
        expected.IAmAliveTime = heartbeat.IAmAliveTime;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(Assert.Single(before.Members).Item2, Assert.Single(after.Members).Item2);
        AssertConformanceEntry(expected, Assert.Single(after.Members).Item1);
        AssertConformanceEntry(entry, Assert.Single(before.Members).Item1);
    }

    [Fact]
    public void CleanupDefunctSiloEntries_UsesStrictMaximumUpdateTimeCutoff()
    {
        var cutoff = new DateTime(2024, 1, 2, 3, 5, 0, DateTimeKind.Utc);
        var entries = Enumerable.Range(1, 5).Select(CreateConformanceEntry).ToArray();
        foreach (var entry in entries)
        {
            entry.Status = SiloStatus.Dead;
        }

        entries[1].StartTime = cutoff.AddSeconds(1);
        entries[2].IAmAliveTime = cutoff.AddSeconds(1);
        entries[3].SuspectTimes![0] = Tuple.Create(entries[0].SiloAddress, cutoff.AddSeconds(1));
        entries[4].IAmAliveTime = cutoff;
        foreach (var entry in entries)
        {
            Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
        }

        var before = table.ReadAll();
        table.CleanupDefunctSiloEntries(new DateTimeOffset(cutoff));

        var after = table.ReadAll();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(4, after.Members.Count);
        Assert.Empty(table.Read(entries[0].SiloAddress).Members);
        foreach (var expected in entries.Skip(1))
        {
            var actual = Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(expected.SiloAddress));
            AssertConformanceEntry(expected, actual.Item1);
            Assert.Equal(before.TryGet(expected.SiloAddress)!.Item2, actual.Item2);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Writes_DetachCallerEntryAndSuspectList(bool update)
    {
        var input = CreateConformanceEntry(1);
        Assert.True(table.Insert(input, table.ReadTableVersion().Next()));
        if (update)
        {
            var current = table.Read(input.SiloAddress);
            input.Status = SiloStatus.Active;
            Assert.True(table.Update(input, Assert.Single(current.Members).Item2, current.Version.Next()));
        }

        var expected = input.Copy();
        var before = table.Read(input.SiloAddress);
        var rowToken = Assert.Single(before.Members).Item2;
        input.Status = SiloStatus.Dead;
        input.HostName = "caller-mutated";
        input.IAmAliveTime = input.IAmAliveTime.AddDays(1);
        input.SuspectTimes!.Clear();
        input.SuspectTimes = [Tuple.Create(CreateConformanceEntry(2).SiloAddress, input.IAmAliveTime)];

        var after = table.Read(input.SiloAddress);
        Assert.Equal(before.Version, after.Version);
        var stored = Assert.Single(after.Members);
        Assert.Equal(rowToken, stored.Item2);
        AssertConformanceEntry(expected, stored.Item1);
        AssertConformanceEntry(expected, Assert.Single(before.Members).Item1);
    }

    [Fact]
    public void CleanupDefunctSiloEntries_RecentDeathVoteProtectsStaleHeartbeat()
    {
        var entry = CreateConformanceEntry(1);
        entry.Status = SiloStatus.Active;
        Assert.True(table.Insert(entry, table.ReadTableVersion().Next()));
        var current = table.Read(entry.SiloAddress);
        var deathTime = entry.IAmAliveTime.AddMinutes(10);
        entry.Status = SiloStatus.Dead;
        entry.SuspectTimes![0] = Tuple.Create(entry.SuspectTimes[0].Item1, deathTime);
        Assert.True(table.Update(entry, Assert.Single(current.Members).Item2, current.Version.Next()));
        var before = table.Read(entry.SiloAddress);

        table.CleanupDefunctSiloEntries(new DateTimeOffset(deathTime));

        var retained = table.Read(entry.SiloAddress);
        Assert.Equal(before.Version, retained.Version);
        Assert.Equal(Assert.Single(before.Members).Item2, Assert.Single(retained.Members).Item2);
        AssertConformanceEntry(entry, Assert.Single(retained.Members).Item1);

        table.CleanupDefunctSiloEntries(new DateTimeOffset(deathTime.AddSeconds(1)));
        var removed = table.Read(entry.SiloAddress);
        Assert.Equal(before.Version, removed.Version);
        Assert.Empty(removed.Members);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupDefunctSiloEntries_PreservesVersionIncludingAtLimit(bool atVersionLimit)
    {
        var entries = Enumerable.Range(1, 3).Select(CreateConformanceEntry).ToArray();
        entries[0].Status = SiloStatus.Dead;
        entries[1].Status = SiloStatus.Dead;
        entries[2].Status = SiloStatus.Active;
        foreach (var entry in entries)
        {
            var current = table.ReadTableVersion();
            // Seed the version limit directly to exercise unversioned cleanup at the boundary.
            var candidate = atVersionLimit && entry == entries[^1]
                ? new TableVersion(int.MaxValue, current.VersionEtag)
                : current.Next();
            Assert.True(table.Insert(entry, candidate));
        }
        var before = table.ReadAll();
        var cutoff = new DateTimeOffset(entries[0].IAmAliveTime.AddMinutes(1));

        table.CleanupDefunctSiloEntries(cutoff);
        var after = table.ReadAll();
        Assert.Equal(before.Version, after.Version);
        var survivor = Assert.Single(after.Members);
        AssertConformanceEntry(entries[2], survivor.Item1);
        Assert.Equal(before.TryGet(entries[2].SiloAddress)!.Item2, survivor.Item2);
        table.CleanupDefunctSiloEntries(cutoff);
        Assert.Equal(after.Version, table.ReadTableVersion());
    }

    private static MembershipEntry CreateConformanceEntry(int index)
    {
        var timestamp = new DateTime(2024, 1, 2, 3, 4, 0, DateTimeKind.Utc);
        return new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 12000 + index, 17),
            SiloName = $"silo-{index}",
            HostName = $"host-{index}",
            RoleName = "worker",
            UpdateZone = 3,
            FaultZone = 5,
            ProxyPort = 22000 + index,
            Status = SiloStatus.Joining,
            StartTime = timestamp.AddMinutes(-1),
            IAmAliveTime = timestamp,
            SuspectTimes = [Tuple.Create(SiloAddress.New(IPAddress.Loopback, 11001, 10), timestamp.AddSeconds(-10))]
        };
    }

    private static void AssertConformanceEntry(MembershipEntry expected, MembershipEntry actual)
    {
        AssertCanonicalFields(expected, actual);
        Assert.Equal(expected.IAmAliveTime, actual.IAmAliveTime);
    }

    private static void AssertCanonicalFields(MembershipEntry expected, MembershipEntry actual)
    {
        Assert.Equal(expected.SiloAddress, actual.SiloAddress);
        Assert.Equal(expected.SiloName, actual.SiloName);
        Assert.Equal(expected.HostName, actual.HostName);
        Assert.Equal(expected.RoleName, actual.RoleName);
        Assert.Equal(expected.UpdateZone, actual.UpdateZone);
        Assert.Equal(expected.FaultZone, actual.FaultZone);
        Assert.Equal(expected.ProxyPort, actual.ProxyPort);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.StartTime, actual.StartTime);
        Assert.Equal(expected.SuspectTimes, actual.SuspectTimes);
    }
}
