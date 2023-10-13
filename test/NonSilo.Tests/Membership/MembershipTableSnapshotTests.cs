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
            var snapshot = MembershipTableSnapshot.Create(
                localSiloEntry,
                Table());

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
            var snapshot = MembershipTableSnapshot.Create(
                localSiloEntry,
                Table(
                    Entry(silo, SiloStatus.Active),
                    Entry(Silo("127.0.0.1:200@1"), SiloStatus.Active)));
            
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
            var snapshot = MembershipTableSnapshot.Create(
                knownSiloEntry,
                Table(knownSiloEntry));

            Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknownSilo));
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_UnknownSilo_KnownSuccessor()
        {
            var unknownSilo = Silo("127.0.0.1:100@1");
            var knownSuccessor = Silo("127.0.0.1:100@2");

            var knownSiloEntry = Entry(knownSuccessor, SiloStatus.Active);
            var snapshot = MembershipTableSnapshot.Create(
                knownSiloEntry,
                Table(knownSiloEntry));

            Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(unknownSilo));
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status)
        {
            return new MembershipEntry { SiloAddress = address, Status = status };
        }

        private static MembershipTableData Table(params MembershipEntry[] entries)
        {
            var entryList = entries.Select(e => Tuple.Create(e, "test")).ToList();
            return new MembershipTableData(entryList, new TableVersion(12, "test"));
        }
    }
}
