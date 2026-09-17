using AwesomeAssertions.Common;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for membership table snapshot functionality including silo status retrieval and IAmAliveTime preservation.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class MembershipTableSnapshotTests
    {
        [Theory]
        [InlineData("status")]
        [InlineData("proxy-port")]
        [InlineData("host")]
        [InlineData("silo-name")]
        [InlineData("role")]
        [InlineData("update-zone")]
        [InlineData("fault-zone")]
        [InlineData("start-time")]
        [InlineData("suspect-vote")]
        public void SameVersionUpdatePreservesCanonicalFields(string field)
        {
            var local = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var dead = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(local, dead));
            var changed = local.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1));
            switch (field)
            {
                case "status":
                    changed.Status = SiloStatus.Dead;
                    break;
                case "proxy-port":
                    changed.ProxyPort = 1234;
                    break;
                case "host":
                    changed.HostName = "changed";
                    break;
                case "silo-name":
                    changed.SiloName = "changed";
                    break;
                case "role":
                    changed.RoleName = "changed";
                    break;
                case "update-zone":
                    changed.UpdateZone = 1;
                    break;
                case "fault-zone":
                    changed.FaultZone = 1;
                    break;
                case "start-time":
                    changed.StartTime = DateTime.UnixEpoch;
                    break;
                case "suspect-vote":
                    changed.AddSuspector(dead.SiloAddress, DateTime.UnixEpoch);
                    break;
                default:
                    throw new InvalidOperationException(field);
            }

            var incoming = MembershipTableSnapshot.Create(Table(changed, dead));

            Assert.All(
                new[] { MembershipTableSnapshot.Update(previous, Table(changed, dead)), MembershipTableSnapshot.Update(previous, incoming) },
                updated =>
                {
                    Assert.Equal(2, updated.Entries.Count);
                    Assert.Contains(dead.SiloAddress, updated.Entries.Keys);
                    var retained = updated.Entries[local.SiloAddress];
                    Assert.Equal(previous.Version, updated.Version);
                    Assert.True(updated.IsSuccessorTo(previous));
                    Assert.Equal(local.SiloAddress, retained.SiloAddress);
                    Assert.Equal(local.Status, retained.Status);
                    Assert.Equal(local.ProxyPort, retained.ProxyPort);
                    Assert.Equal(local.HostName, retained.HostName);
                    Assert.Equal(local.SiloName, retained.SiloName);
                    Assert.Equal(local.RoleName, retained.RoleName);
                    Assert.Equal(local.UpdateZone, retained.UpdateZone);
                    Assert.Equal(local.FaultZone, retained.FaultZone);
                    Assert.Equal(local.StartTime, retained.StartTime);
                    Assert.Null(retained.SuspectTimes);
                    Assert.Equal(changed.IAmAliveTime, retained.IAmAliveTime);
                });
            Assert.Equal(SiloStatus.Active, previous.Entries[local.SiloAddress].Status);
            Assert.Equal(DateTime.UnixEpoch, previous.Entries[local.SiloAddress].IAmAliveTime);
            Assert.Contains(dead.SiloAddress, previous.Entries.Keys);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void SameVersionUpdatePreservesCommittedFieldsAndMaximumHeartbeat(bool fromPeer, bool laterHeartbeat)
        {
            var local = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            local.StartTime = DateTime.UnixEpoch.AddTicks(12345);
            local.HostName = "committed-host";
            var previous = MembershipTableSnapshot.Create(Table(local));
            var persisted = local.WithIAmAliveTime(laterHeartbeat ? DateTime.UnixEpoch.AddMinutes(2) : DateTime.UnixEpoch);
            persisted.StartTime = DateTime.UnixEpoch.AddMilliseconds(1);
            persisted.HostName = "older-field";

            var incoming = Table(persisted);
            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(incoming))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(laterHeartbeat, updated.IsSuccessorTo(previous));
            var retained = Assert.Single(updated.Entries).Value;
            Assert.Equal(local.HostName, retained.HostName);
            Assert.Equal(local.StartTime, retained.StartTime);
            Assert.Equal(laterHeartbeat ? persisted.IAmAliveTime : local.IAmAliveTime, retained.IAmAliveTime);
            Assert.Equal("older-field", persisted.HostName);
        }

        [Theory]
        [InlineData(false, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 1)]
        [InlineData(true, 2)]
        public void NewerVersionUpdateAdvancesCanonicalViewAndPreservesHeartbeat(bool fromPeer, int versionAdvance)
        {
            var local = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            var previousTable = Table(local);
            var previous = MembershipTableSnapshot.Create(previousTable);
            var changed = local.WithStatus(SiloStatus.Dead).WithIAmAliveTime(DateTime.UnixEpoch);
            var suspector = Silo("127.0.0.1:200@1");
            changed.AddSuspector(suspector, DateTime.UnixEpoch);
            var incoming = new MembershipTableData(
                new List<Tuple<MembershipEntry, string>> { Tuple.Create(changed, "updated") },
                new TableVersion(previousTable.Version.Version + versionAdvance, "updated"));
            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(incoming))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(new MembershipVersion(previous.Version.Value + versionAdvance), updated.Version);
            Assert.True(updated.IsSuccessorTo(previous));
            var retained = Assert.Single(updated.Entries).Value;
            Assert.Equal(local.SiloAddress, retained.SiloAddress);
            Assert.Equal(SiloStatus.Dead, retained.Status);
            Assert.NotNull(retained.SuspectTimes);
            var vote = Assert.Single(retained.SuspectTimes);
            Assert.Equal(suspector, vote.Item1);
            Assert.Equal(DateTime.UnixEpoch, vote.Item2);
            Assert.Equal(local.IAmAliveTime, retained.IAmAliveTime);
            Assert.Equal(SiloStatus.Active, previous.Entries[local.SiloAddress].Status);
            Assert.Equal(DateTime.UnixEpoch, changed.IAmAliveTime);
        }

        [Fact]
        public void SameVersionHeartbeatAdvanceIsSuccessorBeforeStartTime()
        {
            var entry = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            entry.StartTime = DateTime.UnixEpoch.AddMinutes(2);
            var previous = MembershipTableSnapshot.Create(Table(entry));
            var incoming = MembershipTableSnapshot.Create(Table(entry.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1))));

            var updated = MembershipTableSnapshot.Update(previous, incoming);

            Assert.True(updated.IsSuccessorTo(previous));
            var retained = Assert.Single(updated.Entries).Value;
            Assert.Equal(entry.StartTime, retained.StartTime);
            Assert.Equal(DateTime.UnixEpoch.AddMinutes(1), retained.IAmAliveTime);
            Assert.Equal(DateTime.UnixEpoch, previous.Entries[entry.SiloAddress].IAmAliveTime);
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_JoiningSilo()
        {
            var silo = Silo("127.0.0.1:100@1");

            // The table is empty
            var localSiloEntry = Entry(silo, SiloStatus.Joining);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(Table()), localSiloEntry);

            Assert.Equal(localSiloEntry.Status, snapshot.GetSiloStatus(silo));
            Assert.Equal(silo, snapshot.Entries[silo].SiloAddress);
            Assert.Equal(localSiloEntry.Status, snapshot.Entries[silo].Status);
            Assert.Contains(snapshot.Entries, e => e.Key.Equals(silo) && e.Value.Status == localSiloEntry.Status);
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_StoppingSilo()
        {
            var silo = Silo("127.0.0.1:100@1");

            // Check that the Silo status in the entry directly provided to the
            // constructor overrides the value in the table.
            var localSiloEntry = Entry(silo, SiloStatus.Stopping);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(
                Table(
                    Entry(silo, SiloStatus.Active),
                    Entry(Silo("127.0.0.1:200@1"), SiloStatus.Active))),
                localSiloEntry);

            Assert.Equal(localSiloEntry.Status, snapshot.GetSiloStatus(silo));
            Assert.Equal(silo, snapshot.Entries[silo].SiloAddress);
            Assert.Equal(localSiloEntry.Status, snapshot.Entries[silo].Status);
            Assert.Contains(snapshot.Entries, e => e.Key.Equals(silo) && e.Value.Status == localSiloEntry.Status);
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_UnknownSilo()
        {
            var knownSilo = Silo("127.0.0.1:100@1");
            var unknownSilo = Silo("127.0.0.1:101@1");

            var knownSiloEntry = Entry(knownSilo, SiloStatus.Active);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(Table(knownSiloEntry)), knownSiloEntry);

            Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknownSilo));
        }

        [Fact]
        public void MembershipTableSnapshot_CreateUpdatePreservesIAmAliveTime()
        {
            var originalSilo = Silo("127.0.0.1:100@1");
            var earlierDate = new DateTimeOffset(new DateTime(2025, 1, 30, 12, 30, 45, DateTimeKind.Utc));
            var laterDate = earlierDate.AddDays(1);

            // Merging a later snapshot with an earlier date with an older snapshot with a later date should preserve the later date.
            {
                var originalSnapshot = MembershipTableSnapshot.Create(Table(Entry(originalSilo, SiloStatus.Active, laterDate)));
                var newSnapshot = MembershipTableSnapshot.Update(originalSnapshot, Table(Entry(originalSilo, SiloStatus.Active, earlierDate)));

                var iAmAliveTime = newSnapshot.Entries[originalSilo].IAmAliveTime;
                Assert.Equal(laterDate, iAmAliveTime);
            }

            // Now do the same thing, but using a snapshot instead of a table
            {
                var originalSnapshot = MembershipTableSnapshot.Create(Table(Entry(originalSilo, SiloStatus.Active, laterDate)));
                var newSnapshot = MembershipTableSnapshot.Update(originalSnapshot, MembershipTableSnapshot.Create(Table(Entry(originalSilo, SiloStatus.Active, earlierDate))));

                var iAmAliveTime = newSnapshot.Entries[originalSilo].IAmAliveTime;
                Assert.Equal(laterDate, iAmAliveTime);
            }
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_UnknownSilo_KnownSuccessor()
        {
            var unknownSilo = Silo("127.0.0.1:100@1");
            var knownSuccessor = Silo("127.0.0.1:100@2");

            var knownSiloEntry = Entry(knownSuccessor, SiloStatus.Active);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(Table(knownSiloEntry)), knownSiloEntry);

            Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(unknownSilo));
        }

        [Fact]
        public void MembershipTableSnapshot_TryFormat_MatchesToString()
        {
            var silo = Silo("127.0.0.1:100@1");
            var snapshot = MembershipTableSnapshot.Create(Table(Entry(silo, SiloStatus.Active)));

            AssertSpanFormattable(snapshot);
            AssertSpanFormattable(snapshot.Version);
            AssertSpanFormattable(snapshot.Entries[silo]);
            AssertSpanFormattable(silo);
            AssertSpanFormattable(silo, "H");
            AssertSpanFormattable(MembershipVersion.MinValue);
        }

        [Theory]
        [InlineData(SiloStatus.Created)]
        [InlineData(SiloStatus.Joining)]
        [InlineData(SiloStatus.Active)]
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        [InlineData(SiloStatus.Dead)]
        public void SameVersionRowRemovalIsNotASuccessor(SiloStatus removedStatus)
        {
            var active = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            var removed = Entry(Silo("127.0.0.1:200@1"), removedStatus, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(active, removed));
            var incoming = MembershipTableSnapshot.Create(Table(active));
            var updated = MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(previous.Version, updated.Version);
            Assert.False(incoming.IsSuccessorTo(previous));
            Assert.False(updated.IsSuccessorTo(previous));
            Assert.False(previous.IsSuccessorTo(updated));
            Assert.Contains(removed.SiloAddress, previous.Entries.Keys);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void VersionedDeadRowRemovalPreservesNewerLocalHeartbeat(bool fromPeer)
        {
            var address = Silo("127.0.0.1:100@1");
            var newer = DateTimeOffset.UnixEpoch.AddMinutes(2);
            var older = DateTimeOffset.UnixEpoch.AddMinutes(1);
            var previousTable = Table(
                Entry(address, SiloStatus.Active, newer),
                Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead));
            var previous = MembershipTableSnapshot.Create(previousTable);
            var retained = Entry(address, SiloStatus.Active, older);
            var incoming = new MembershipTableData(Tuple.Create(retained, "updated"), previousTable.Version.Next());
            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(incoming))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(new MembershipVersion(previous.Version.Value + 1), updated.Version);
            Assert.True(updated.IsSuccessorTo(previous));
            Assert.Equal(newer.UtcDateTime, Assert.Single(updated.Entries).Value.IAmAliveTime);
            Assert.Equal(older.UtcDateTime, retained.IAmAliveTime);
            Assert.Equal(2, previous.Entries.Count);
        }

        [Fact]
        public void SameVersionRowReplacementIsNotASuccessor()
        {
            var active = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active);
            var dead = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead);
            var previous = MembershipTableSnapshot.Create(Table(active, dead));
            var replacedEntry = MembershipTableSnapshot.Create(Table(
                active.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1)),
                Entry(Silo("127.0.0.1:300@1"), SiloStatus.Dead)));

            Assert.Equal(previous.Entries.Count, replacedEntry.Entries.Count);
            Assert.False(replacedEntry.IsSuccessorTo(previous));
            Assert.Equal(SiloStatus.Active, previous.Entries[active.SiloAddress].Status);
        }

        [Fact]
        public void EmptyCanonicalViewRequiresVersionAdvance()
        {
            var previous = MembershipTableSnapshot.Create(Table(
                Entry(Silo("127.0.0.1:100@1"), SiloStatus.Dead),
                Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead)));
            var empty = MembershipTableSnapshot.Create(Table());

            Assert.Equal(previous.Version, empty.Version);
            Assert.False(empty.IsSuccessorTo(previous));
            Assert.True(new MembershipTableSnapshot(new MembershipVersion(previous.Version.Value + 1), empty.Entries)
                .IsSuccessorTo(previous));
            Assert.False(empty.IsSuccessorTo(empty));
            Assert.Empty(empty.Entries);
            Assert.Equal(2, previous.Entries.Count);
        }

        [Fact]
        public void SameVersionHeartbeatAdvanceRequiresCompleteCanonicalRowset()
        {
            var keep = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var active = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var dead = Entry(Silo("127.0.0.1:300@1"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(keep, active, dead));
            var later = keep.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1));

            Assert.False(MembershipTableSnapshot.Create(Table(later, dead)).IsSuccessorTo(previous));
            Assert.False(MembershipTableSnapshot.Create(Table(later, active)).IsSuccessorTo(previous));
            Assert.True(MembershipTableSnapshot.Create(Table(later, active, dead)).IsSuccessorTo(previous));
            Assert.Equal(DateTime.UnixEpoch, previous.Entries[keep.SiloAddress].IAmAliveTime);
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTimeOffset iAmAliveTime = default)
        {
            return new MembershipEntry { SiloAddress = address, Status = status, IAmAliveTime = iAmAliveTime.UtcDateTime };
        }

        private static MembershipTableData Table(params MembershipEntry[] entries)
        {
            var entryList = entries.Select(e => Tuple.Create(e, "test")).ToList();
            return new MembershipTableData(entryList, new TableVersion(12, "test"));
        }

        private static MembershipTableSnapshot AddOrUpdateEntry(MembershipTableSnapshot table, MembershipEntry localSiloEntry)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));

            var entries = table.Entries.ToBuilder();

            if (entries.TryGetValue(localSiloEntry.SiloAddress, out var existing))
            {
                entries[localSiloEntry.SiloAddress] = existing.WithStatus(localSiloEntry.Status);
            }
            else
            {
                entries[localSiloEntry.SiloAddress] = localSiloEntry;
            }

            return new MembershipTableSnapshot(table.Version, entries.ToImmutable());
        }

        private static void AssertSpanFormattable(ISpanFormattable value, string? format = null)
        {
            var expected = value.ToString(format, null);
            var formatSpan = format is null ? default : format.AsSpan();
            Span<char> destination = stackalloc char[expected.Length];

            Assert.True(value.TryFormat(destination, out var charsWritten, formatSpan, null));
            Assert.Equal(expected.Length, charsWritten);
            Assert.Equal(expected, destination[..charsWritten].ToString());

            if (expected.Length > 0)
            {
                Span<char> tooSmall = stackalloc char[expected.Length - 1];
                Assert.False(value.TryFormat(tooSmall, out charsWritten, formatSpan, null));
                Assert.Equal(0, charsWritten);
            }
        }

    }

}
