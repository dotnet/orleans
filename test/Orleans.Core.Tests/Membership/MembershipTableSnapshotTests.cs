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
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        [InlineData(SiloStatus.Dead)]
        public void MembershipTableSnapshot_InventoryCleanupIsSuccessorWithoutHeartbeat(SiloStatus removedStatus)
        {
            var active = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            var removed = Entry(Silo("127.0.0.1:200@1"), removedStatus, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(active, removed));
            var cleaned = MembershipTableSnapshot.Update(previous, Table(active));

            Assert.Equal(previous.Version, cleaned.Version);
            Assert.Equal(previous.Entries[active.SiloAddress].IAmAliveTime, cleaned.Entries[active.SiloAddress].IAmAliveTime);
            Assert.True(cleaned.IsSuccessorTo(previous));
            Assert.False(previous.IsSuccessorTo(cleaned));
            Assert.False(cleaned.IsSuccessorTo(cleaned));
            Assert.Equal(1, cleaned.ActiveNodeCount);
            Assert.DoesNotContain(removed.SiloAddress, cleaned.Entries.Keys);
            Assert.Contains(removed.SiloAddress, previous.Entries.Keys);
        }

        [Fact]
        public void MembershipTableSnapshot_InventoryCleanupPreservesNewerLocalHeartbeat()
        {
            var address = Silo("127.0.0.1:100@1");
            var newer = DateTimeOffset.UnixEpoch.AddMinutes(2);
            var older = DateTimeOffset.UnixEpoch.AddMinutes(1);
            var previous = MembershipTableSnapshot.Create(Table(
                Entry(address, SiloStatus.Active, newer),
                Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead)));
            var incoming = MembershipTableSnapshot.Create(Table(Entry(address, SiloStatus.Active, older)));
            var merged = MembershipTableSnapshot.Update(previous, incoming);

            Assert.True(incoming.IsSuccessorTo(previous));
            Assert.True(merged.IsSuccessorTo(previous));
            Assert.Equal(newer.UtcDateTime, merged.Entries[address].IAmAliveTime);
            Assert.Equal(older.UtcDateTime, incoming.Entries[address].IAmAliveTime);
            Assert.Single(merged.Entries);
        }

        [Fact]
        public void MembershipTableSnapshot_InventoryCleanupCannotChangeActiveMembershipOrRetainedStatus()
        {
            var active = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active);
            var dead = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead);
            var previous = MembershipTableSnapshot.Create(Table(active, dead));
            var removedActive = MembershipTableSnapshot.Create(Table(dead));
            var changedStatus = MembershipTableSnapshot.Create(Table(Entry(active.SiloAddress, SiloStatus.Dead)));
            var replacedEntry = MembershipTableSnapshot.Create(Table(Entry(Silo("127.0.0.1:300@1"), SiloStatus.Active)));

            Assert.False(removedActive.IsSuccessorTo(previous));
            Assert.False(changedStatus.IsSuccessorTo(previous));
            Assert.False(replacedEntry.IsSuccessorTo(previous));
            var inactive = MembershipTableSnapshot.Create(Table(
                dead, Entry(Silo("127.0.0.1:300@1"), SiloStatus.Dead)));
            var revived = MembershipTableSnapshot.Create(Table(Entry(dead.SiloAddress, SiloStatus.Active)));
            Assert.False(revived.IsSuccessorTo(inactive));
            Assert.True(new MembershipTableSnapshot(new MembershipVersion(previous.Version.Value + 1), removedActive.Entries)
                .IsSuccessorTo(previous));
            Assert.Equal(SiloStatus.Active, previous.Entries[active.SiloAddress].Status);
        }

        [Fact]
        public void MembershipTableSnapshot_InventoryCleanupCanRemoveAllInactiveEntries()
        {
            var previous = MembershipTableSnapshot.Create(Table(
                Entry(Silo("127.0.0.1:100@1"), SiloStatus.Dead),
                Entry(Silo("127.0.0.1:200@1"), SiloStatus.Stopping)));
            var empty = MembershipTableSnapshot.Create(Table());

            Assert.Equal(previous.Version, empty.Version);
            Assert.True(empty.IsSuccessorTo(previous));
            Assert.False(empty.IsSuccessorTo(empty));
            Assert.Empty(empty.Entries);
            Assert.Equal(2, previous.Entries.Count);
        }

        [Fact]
        public void MembershipTableSnapshot_HeartbeatAdvanceCannotChangeSameVersionMembership()
        {
            var keep = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var active = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var dead = Entry(Silo("127.0.0.1:300@1"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(keep, active, dead));
            var later = keep.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1));

            Assert.False(MembershipTableSnapshot.Create(Table(later, dead)).IsSuccessorTo(previous));
            Assert.False(MembershipTableSnapshot.Create(Table(later, active.WithStatus(SiloStatus.Dead), dead))
                .IsSuccessorTo(previous));
            Assert.False(MembershipTableSnapshot.Create(Table(later, active, dead.WithStatus(SiloStatus.Active)))
                .IsSuccessorTo(previous));
            Assert.True(MembershipTableSnapshot.Create(Table(later, active)).IsSuccessorTo(previous));
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
