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

    [Fact]
    public void WriteResults_ChainReturnedTokensWithoutRead()
    {
        var seed = table.ReadAll();
        Assert.Empty(seed.Members);
        Assert.Equal(new TableVersion(0, "0"), seed.Version);
        var insertA = CreateConformanceEntry(1);
        var updateA = insertA.Copy();
        updateA.Status = SiloStatus.Active;
        updateA.HostName = "updated-host";
        updateA.RoleName = "coordinator";
        updateA.UpdateZone = 7;
        updateA.FaultZone = 9;
        updateA.ProxyPort = 23001;
        updateA.IAmAliveTime = updateA.IAmAliveTime.AddMinutes(3);
        updateA.SuspectTimes![0] = Tuple.Create(CreateConformanceEntry(3).SiloAddress, updateA.IAmAliveTime);
        var expectedA = updateA.Copy();
        var insertB = CreateConformanceEntry(2);
        var expectedB = insertB.Copy();

        // Only raw receipts supply the next operation's tokens; there are no reads in this chain.
        var insertedA = table.InsertWithResult(insertA, seed.Version.Next());
        Assert.True(insertedA.Succeeded);
        var firstReceipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(insertedA.Receipt);
        var firstFields = (firstReceipt.Version.Version, firstReceipt.Version.VersionEtag, firstReceipt.RowETag);
        Assert.Equal((1, "2", "1"), firstFields);
        insertA.Status = SiloStatus.Dead;
        insertA.HostName = "caller-only-insert";
        insertA.IAmAliveTime = insertA.IAmAliveTime.AddDays(1);
        insertA.SuspectTimes!.Clear();

        var updatedA = table.UpdateWithResult(updateA, firstReceipt.RowETag, firstReceipt.Version.Next());
        Assert.True(updatedA.Succeeded);
        var secondReceipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(updatedA.Receipt);
        var secondFields = (secondReceipt.Version.Version, secondReceipt.Version.VersionEtag, secondReceipt.RowETag);
        Assert.Equal((2, "4", "3"), secondFields);
        updateA.Status = SiloStatus.Dead;
        updateA.HostName = "caller-only-update";
        updateA.IAmAliveTime = updateA.IAmAliveTime.AddDays(1);
        updateA.SuspectTimes!.Clear();

        var insertedB = table.InsertWithResult(insertB, secondReceipt.Version.Next());
        Assert.True(insertedB.Succeeded);
        var thirdReceipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(insertedB.Receipt);
        Assert.Equal((3, "6", "5"), (thirdReceipt.Version.Version, thirdReceipt.Version.VersionEtag, thirdReceipt.RowETag));
        insertB.Status = SiloStatus.Dead;
        insertB.HostName = "caller-only-second-insert";
        insertB.IAmAliveTime = insertB.IAmAliveTime.AddDays(1);
        insertB.SuspectTimes!.Clear();

        Assert.Equal(firstFields, (firstReceipt.Version.Version, firstReceipt.Version.VersionEtag, firstReceipt.RowETag));
        Assert.Equal(secondFields, (secondReceipt.Version.Version, secondReceipt.Version.VersionEtag, secondReceipt.RowETag));
        Assert.NotSame(firstReceipt.Version, secondReceipt.Version);
        Assert.NotSame(secondReceipt.Version, thirdReceipt.Version);
        Assert.NotEqual(firstReceipt.RowETag, firstReceipt.Version.VersionEtag);
        Assert.NotEqual(secondReceipt.RowETag, secondReceipt.Version.VersionEtag);
        Assert.NotEqual(thirdReceipt.RowETag, thirdReceipt.Version.VersionEtag);

        var after = table.ReadAll();
        Assert.Equal(2, after.Members.Count);
        Assert.Equal(thirdReceipt.Version, after.Version);
        var storedA = Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(expectedA.SiloAddress));
        var storedB = Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(expectedB.SiloAddress));
        Assert.Equal(secondReceipt.RowETag, storedA.Item2);
        Assert.Equal(thirdReceipt.RowETag, storedB.Item2);
        AssertConformanceEntry(expectedA, storedA.Item1);
        AssertConformanceEntry(expectedB, storedB.Item1);
        Assert.NotSame(updateA, storedA.Item1);
        Assert.NotSame(updateA.SuspectTimes, storedA.Item1.SuspectTimes);
        Assert.NotSame(insertB, storedB.Item1);
        Assert.NotSame(insertB.SuspectTimes, storedB.Item1.SuspectTimes);
    }

    [Theory]
    [InlineData("F1")] // Duplicate insert, current table token, after heartbeat.
    [InlineData("F2")] // Absent insert, stale table token, after another row's heartbeat.
    [InlineData("F3")] // Missing update, current table token.
    [InlineData("F4")] // Update after cleanup, still-current table token.
    [InlineData("F5")] // Existing update, stale table token and current row guard, after heartbeat.
    [InlineData("F6")] // Existing update, current table token and wrong local row guard, after heartbeat.
    public void WriteResult_ConditionalFailuresLeaveStateUnchanged(string scenario)
    {
        var seed = table.ReadAll();
        var neighbor = CreateConformanceEntry(2);
        neighbor.Status = SiloStatus.Active;
        var neighborResult = table.InsertWithResult(neighbor, seed.Version.Next());
        Assert.True(neighborResult.Succeeded);
        var neighborReceipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(neighborResult.Receipt);
        Assert.Equal((1, "2", "1"), (neighborReceipt.Version.Version, neighborReceipt.Version.VersionEtag, neighborReceipt.RowETag));

        var entry = CreateConformanceEntry(1);
        if (scenario == "F4")
        {
            entry.Status = SiloStatus.Dead;
        }

        var inserted = table.InsertWithResult(entry, neighborReceipt.Version.Next());
        Assert.True(inserted.Succeeded);
        var receipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(inserted.Receipt);
        Assert.Equal((2, "4", "3"), (receipt.Version.Version, receipt.Version.VersionEtag, receipt.RowETag));
        var expectedBefore = entry.Copy();
        if (scenario is "F1" or "F2" or "F5" or "F6")
        {
            expectedBefore.IAmAliveTime = entry.IAmAliveTime.AddMinutes(2);
            table.UpdateIAmAlive(new MembershipEntry
            {
                SiloAddress = entry.SiloAddress,
                IAmAliveTime = expectedBefore.IAmAliveTime
            });
        }
        else if (scenario == "F4")
        {
            table.CleanupDefunctSiloEntries(new DateTimeOffset(entry.IAmAliveTime.AddMinutes(1)));
        }

        // Capture the rejection baseline after heartbeat/cleanup, not before preparation.
        var before = table.ReadAll();
        Assert.Equal(new TableVersion(2, "4"), before.Version);
        Assert.Equal(scenario == "F4" ? 1 : 2, before.Members.Count);
        var neighborBefore = Assert.Single(before.Members, row => row.Item1.SiloAddress.Equals(neighbor.SiloAddress));
        Assert.Equal(neighborReceipt.RowETag, neighborBefore.Item2);
        AssertConformanceEntry(neighbor, neighborBefore.Item1);
        if (scenario == "F4")
        {
            Assert.Null(before.TryGet(entry.SiloAddress));
        }
        else
        {
            var stored = Assert.Single(before.Members, row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
            Assert.Equal(receipt.RowETag, stored.Item2);
            AssertConformanceEntry(expectedBefore, stored.Item1);
        }

        var attempt = scenario is "F2" or "F3" ? CreateConformanceEntry(3) : entry.Copy();
        attempt.Status = SiloStatus.Active;
        attempt.HostName = "attempted-host";
        attempt.IAmAliveTime = attempt.IAmAliveTime.AddMinutes(1);
        attempt.SuspectTimes![0] = Tuple.Create(neighbor.SiloAddress, attempt.IAmAliveTime);
        var expectedAttempt = attempt.Copy();
        var validVersion = receipt.Version.Next();
        // Keep the requested integer identical: only the table CAS tag is stale in F2/F5.
        var attemptedVersion = scenario is "F2" or "F5"
            ? new TableVersion(validVersion.Version, neighborReceipt.Version.VersionEtag)
            : validVersion;
        // This native table's additional canonical guard is not a universal provider rule.
        var attemptedGuard = scenario == "F6" ? neighborReceipt.RowETag : receipt.RowETag;

        var rejected = scenario is "F1" or "F2"
            ? table.InsertWithResult(attempt, attemptedVersion)
            : table.UpdateWithResult(attempt, attemptedGuard, attemptedVersion);

        Assert.False(rejected.Succeeded);
        Assert.Null(rejected.Receipt);
        AssertConformanceEntry(expectedAttempt, attempt);
        var after = table.ReadAll();
        Assert.Equal(before.Version.Version, after.Version.Version);
        Assert.Equal(before.Version.VersionEtag, after.Version.VersionEtag);
        Assert.Equal(before.Members.Count, after.Members.Count);
        foreach (var expected in before.Members)
        {
            var actual = Assert.Single(after.Members, row => row.Item1.SiloAddress.Equals(expected.Item1.SiloAddress));
            Assert.Equal(expected.Item2, actual.Item2);
            AssertConformanceEntry(expected.Item1, actual.Item1);
        }

        var insertContinuation = scenario is "F2" or "F3" or "F4";
        if (insertContinuation)
        {
            Assert.Null(before.TryGet(attempt.SiloAddress));
            Assert.Null(after.TryGet(attempt.SiloAddress));
        }

        // Reuse the original valid tokens, without replacing them from the assertion read.
        var continued = insertContinuation
            ? table.InsertWithResult(attempt, validVersion)
            : table.UpdateWithResult(attempt, receipt.RowETag, validVersion);
        Assert.True(continued.Succeeded);
        var continuedReceipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(continued.Receipt);
        // Two seed writes consumed tags 1..4; rejection, heartbeat and cleanup consumed none.
        Assert.Equal((3, "6", "5"), (continuedReceipt.Version.Version, continuedReceipt.Version.VersionEtag, continuedReceipt.RowETag));
        Assert.Equal((2, "4", "3"), (receipt.Version.Version, receipt.Version.VersionEtag, receipt.RowETag));
        Assert.Equal((1, "2", "1"), (neighborReceipt.Version.Version, neighborReceipt.Version.VersionEtag, neighborReceipt.RowETag));
        var committed = table.ReadAll();
        Assert.Equal(continuedReceipt.Version, committed.Version);
        Assert.Equal(before.Members.Count + (insertContinuation ? 1 : 0), committed.Members.Count);
        var written = Assert.Single(committed.Members, row => row.Item1.SiloAddress.Equals(attempt.SiloAddress));
        Assert.Equal(continuedReceipt.RowETag, written.Item2);
        AssertConformanceEntry(expectedAttempt, written.Item1);
        AssertConformanceEntry(expectedAttempt, attempt);
        foreach (var expected in before.Members.Where(row => !row.Item1.SiloAddress.Equals(attempt.SiloAddress)))
        {
            var actual = Assert.Single(committed.Members, row => row.Item1.SiloAddress.Equals(expected.Item1.SiloAddress));
            Assert.Equal(expected.Item2, actual.Item2);
            AssertConformanceEntry(expected.Item1, actual.Item1);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void WriteResult_PreHeartbeatTokensRemainUsable(int clockOffsetMinutes)
    {
        var entry = CreateConformanceEntry(1);
        var original = entry.Copy();
        var seed = table.ReadAll();
        var inserted = table.InsertWithResult(entry, seed.Version.Next());
        Assert.True(inserted.Succeeded);
        var receipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(inserted.Receipt);
        var receiptFields = (receipt.Version.Version, receipt.Version.VersionEtag, receipt.RowETag);
        Assert.Equal((1, "2", "1"), receiptFields);
        var heartbeat = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            IAmAliveTime = entry.IAmAliveTime.AddMinutes(clockOffsetMinutes)
        };

        table.UpdateIAmAlive(heartbeat);

        var afterHeartbeat = table.ReadAll();
        var heartbeatingRow = Assert.Single(afterHeartbeat.Members);
        Assert.Equal(receipt.Version, afterHeartbeat.Version);
        Assert.Equal(receipt.RowETag, heartbeatingRow.Item2);
        AssertCanonicalFields(original, heartbeatingRow.Item1);
        Assert.Equal(heartbeat.IAmAliveTime, heartbeatingRow.Item1.IAmAliveTime);
        AssertConformanceEntry(original, entry);

        var update = original.Copy();
        update.Status = SiloStatus.Active;
        update.HostName = "post-heartbeat-host";
        update.RoleName = "coordinator";
        update.ProxyPort = 23001;
        update.SuspectTimes!.Add(Tuple.Create(CreateConformanceEntry(2).SiloAddress, original.IAmAliveTime.AddSeconds(30)));
        var expected = update.Copy();

        // The local canonical row guard and table token were both obtained before the heartbeat.
        var updated = table.UpdateWithResult(update, receipt.RowETag, receipt.Version.Next());

        Assert.True(updated.Succeeded);
        var updatedReceipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(updated.Receipt);
        Assert.Equal((2, "4", "3"), (updatedReceipt.Version.Version, updatedReceipt.Version.VersionEtag, updatedReceipt.RowETag));
        Assert.Equal(receiptFields, (receipt.Version.Version, receipt.Version.VersionEtag, receipt.RowETag));
        AssertConformanceEntry(original, entry);
        AssertConformanceEntry(expected, update);
        var after = table.ReadAll();
        var stored = Assert.Single(after.Members);
        Assert.Equal(updatedReceipt.Version, after.Version);
        Assert.Equal(updatedReceipt.RowETag, stored.Item2);
        // A canonical update copies its input heartbeat, rather than merging the latest owner report.
        AssertConformanceEntry(expected, stored.Item1);
        Assert.NotSame(update, stored.Item1);
        Assert.NotSame(update.SuspectTimes, stored.Item1.SuspectTimes);
        Assert.Equal(new TableVersion(1, "2"), afterHeartbeat.Version);
        Assert.Equal("1", heartbeatingRow.Item2);
        AssertCanonicalFields(original, heartbeatingRow.Item1);
        Assert.Equal(original.IAmAliveTime.AddMinutes(clockOffsetMinutes), heartbeatingRow.Item1.IAmAliveTime);
        Assert.Equal(original.IAmAliveTime.AddMinutes(clockOffsetMinutes), heartbeat.IAmAliveTime);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void WriteResult_AndBoolEntryPoints_UseEquivalentMutationSemantics(bool update, bool accepted)
    {
        var boolTable = new Orleans.Runtime.MembershipService.InMemoryMembershipTable(
            _services.GetRequiredService<Orleans.Serialization.DeepCopier>());
        var richTable = table;
        var boolSeed = boolTable.ReadTableVersion();
        var richSeed = richTable.ReadTableVersion();
        var entries = new[] { CreateConformanceEntry(1), CreateConformanceEntry(2) };
        foreach (var entry in entries)
        {
            Assert.True(boolTable.Insert(entry.Copy(), boolTable.ReadTableVersion().Next()));
            Assert.True(richTable.Insert(entry.Copy(), richTable.ReadTableVersion().Next()));
        }

        var boolBefore = boolTable.ReadAll();
        var richBefore = richTable.ReadAll();
        Assert.Equal(new TableVersion(2, "4"), boolBefore.Version);
        Assert.Equal(boolBefore.Version, richBefore.Version);
        Assert.Equal(2, boolBefore.Members.Count);
        Assert.Equal(2, richBefore.Members.Count);
        var expected = CreateConformanceEntry(!update && accepted ? 3 : 1);
        expected.Status = SiloStatus.Active;
        expected.HostName = "parity-host";
        expected.RoleName = "coordinator";
        expected.UpdateZone = 7;
        expected.IAmAliveTime = expected.IAmAliveTime.AddMinutes(2);
        expected.SuspectTimes![0] = Tuple.Create(entries[1].SiloAddress, expected.IAmAliveTime);
        var boolInput = expected.Copy();
        var richInput = expected.Copy();
        var boolRow = Assert.Single(boolBefore.Members, row => row.Item1.SiloAddress.Equals(entries[0].SiloAddress));
        var richRow = Assert.Single(richBefore.Members, row => row.Item1.SiloAddress.Equals(entries[0].SiloAddress));
        Assert.Equal("1", boolRow.Item2);
        Assert.Equal("1", richRow.Item2);
        var boolVersion = update && !accepted
            ? new TableVersion(boolBefore.Version.Version + 1, boolSeed.VersionEtag)
            : boolBefore.Version.Next();
        var richVersion = update && !accepted
            ? new TableVersion(richBefore.Version.Version + 1, richSeed.VersionEtag)
            : richBefore.Version.Next();

        var boolResult = update
            ? boolTable.Update(boolInput, boolRow.Item2, boolVersion)
            : boolTable.Insert(boolInput, boolVersion);
        var richResult = update
            ? richTable.UpdateWithResult(richInput, richRow.Item2, richVersion)
            : richTable.InsertWithResult(richInput, richVersion);

        Assert.Equal(accepted, boolResult);
        Assert.Equal(boolResult, richResult.Succeeded);
        AssertConformanceEntry(expected, boolInput);
        AssertConformanceEntry(expected, richInput);
        var boolAfter = boolTable.ReadAll();
        var richAfter = richTable.ReadAll();
        Assert.Equal(accepted ? new TableVersion(3, "6") : new TableVersion(2, "4"), richAfter.Version);
        Assert.Equal(richAfter.Version, boolAfter.Version);
        Assert.Equal(!update && accepted ? 3 : 2, richAfter.Members.Count);
        Assert.Equal(richAfter.Members.Count, boolAfter.Members.Count);
        if (accepted)
        {
            var receipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(richResult.Receipt);
            Assert.Equal((3, "6", "5"), (receipt.Version.Version, receipt.Version.VersionEtag, receipt.RowETag));
            Assert.Equal(receipt.Version, richAfter.Version);
            var written = Assert.Single(richAfter.Members, row => row.Item1.SiloAddress.Equals(expected.SiloAddress));
            Assert.Equal(receipt.RowETag, written.Item2);
            AssertConformanceEntry(expected, written.Item1);
        }
        else
        {
            Assert.Null(richResult.Receipt);
            Assert.Equal(boolBefore.Version, boolAfter.Version);
            Assert.Equal(richBefore.Version, richAfter.Version);
        }

        foreach (var actual in richAfter.Members)
        {
            var boolActual = Assert.Single(boolAfter.Members, row => row.Item1.SiloAddress.Equals(actual.Item1.SiloAddress));
            Assert.Equal(actual.Item2, boolActual.Item2);
            AssertConformanceEntry(actual.Item1, boolActual.Item1);
            if (accepted && actual.Item1.SiloAddress.Equals(expected.SiloAddress))
            {
                AssertConformanceEntry(expected, actual.Item1);
                Assert.Equal("5", actual.Item2);
            }
            else
            {
                var original = Assert.Single(entries, entry => entry.SiloAddress.Equals(actual.Item1.SiloAddress));
                AssertConformanceEntry(original, actual.Item1);
                Assert.Equal(original.SiloAddress.Equals(entries[0].SiloAddress) ? "1" : "3", actual.Item2);
            }
        }

        if (!accepted)
        {
            // Both rejected operations leave the original valid tokens usable, without tag allocation.
            Assert.True(boolTable.Update(boolInput, boolRow.Item2, boolBefore.Version.Next()));
            var continued = richTable.UpdateWithResult(richInput, richRow.Item2, richBefore.Version.Next());
            Assert.True(continued.Succeeded);
            var receipt = Assert.IsType<Orleans.MembershipTableWriteReceipt>(continued.Receipt);
            Assert.Equal((3, "6", "5"), (receipt.Version.Version, receipt.Version.VersionEtag, receipt.RowETag));
            var boolCommitted = boolTable.ReadAll();
            var richCommitted = richTable.ReadAll();
            Assert.Equal(receipt.Version, richCommitted.Version);
            Assert.Equal(richCommitted.Version, boolCommitted.Version);
            Assert.Equal(2, boolCommitted.Members.Count);
            Assert.Equal(2, richCommitted.Members.Count);
            foreach (var actual in richCommitted.Members)
            {
                var boolActual = Assert.Single(boolCommitted.Members, row => row.Item1.SiloAddress.Equals(actual.Item1.SiloAddress));
                Assert.Equal(actual.Item2, boolActual.Item2);
                AssertConformanceEntry(actual.Item1, boolActual.Item1);
                var written = actual.Item1.SiloAddress.Equals(expected.SiloAddress);
                Assert.Equal(written ? receipt.RowETag : "3", actual.Item2);
                AssertConformanceEntry(written ? expected : entries[1], actual.Item1);
            }

            AssertConformanceEntry(expected, boolInput);
            AssertConformanceEntry(expected, richInput);
        }
    }
}
