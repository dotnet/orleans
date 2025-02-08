using FluentAssertions.Common;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
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

    }

}
