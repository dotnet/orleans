using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests for the in-memory membership table used by development clustering.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("BVT"), TestCategory("Membership")]
    public class InMemoryMembershipTableTests
    {
        private readonly InMemoryMembershipTable table;

        public InMemoryMembershipTableTests()
        {
            var services = new ServiceCollection();
            services.AddSerializer();
            var serviceProvider = services.BuildServiceProvider();
            var deepCopier = serviceProvider.GetRequiredService<DeepCopier>();
            table = new InMemoryMembershipTable(deepCopier);
        }

        [Fact]
        public void CleanupDefunctSiloEntries_RemovesNonActiveOldEntries()
        {
            var tableVersion = table.ReadTableVersion();

            // Add old dead entry (should be removed)
            var deadEntry = CreateEntry(SiloStatus.Dead, daysOld: 10);
            table.Insert(deadEntry, tableVersion);
            tableVersion = table.ReadTableVersion();

            // Add old joining entry (should be removed)
            var joiningEntry = CreateEntry(SiloStatus.Joining, daysOld: 10);
            table.Insert(joiningEntry, tableVersion);
            tableVersion = table.ReadTableVersion();

            // Add old active entry (should NOT be removed)
            var activeEntry = CreateEntry(SiloStatus.Active, daysOld: 10);
            table.Insert(activeEntry, tableVersion);
            tableVersion = table.ReadTableVersion();

            // Add new entry with current timestamp (should NOT be removed regardless of status)
            var newEntry = CreateEntry(SiloStatus.Dead, daysOld: 0);
            table.Insert(newEntry, tableVersion);

            var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
            table.CleanupDefunctSiloEntries(cutoff);

            var data = table.ReadAll();
            Assert.Equal(2, data.Members.Count);
            Assert.Contains(data.Members, m => m.Item1.SiloAddress.Equals(activeEntry.SiloAddress));
            Assert.Contains(data.Members, m => m.Item1.SiloAddress.Equals(newEntry.SiloAddress));
            Assert.DoesNotContain(data.Members, m => m.Item1.SiloAddress.Equals(deadEntry.SiloAddress));
            Assert.DoesNotContain(data.Members, m => m.Item1.SiloAddress.Equals(joiningEntry.SiloAddress));
        }

        [Fact]
        public void CleanupDefunctSiloEntries_RemovesAllNonActiveStatuses()
        {
            foreach (var status in Enum.GetValues<SiloStatus>())
            {
                if (status == SiloStatus.Active)
                {
                    continue;
                }

                var entry = CreateEntry(status, daysOld: 10);
                table.Insert(entry, table.ReadTableVersion());
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
            table.CleanupDefunctSiloEntries(cutoff);

            var data = table.ReadAll();
            Assert.Empty(data.Members);
        }

        [Fact]
        public void CleanupDefunctSiloEntries_PreservesActiveEntries()
        {
            var tableVersion = table.ReadTableVersion();
            var activeEntry = CreateEntry(SiloStatus.Active, daysOld: 30);
            table.Insert(activeEntry, tableVersion);

            var cutoff = DateTimeOffset.UtcNow.AddDays(-5);
            table.CleanupDefunctSiloEntries(cutoff);

            var data = table.ReadAll();
            Assert.Single(data.Members);
            Assert.Equal(activeEntry.SiloAddress, data.Members[0].Item1.SiloAddress);
        }

        private static int _portCounter = 10000;

        private static MembershipEntry CreateEntry(SiloStatus status, int daysOld)
        {
            var port = Interlocked.Increment(ref _portCounter);
            var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), 0);
            var now = DateTime.UtcNow.AddDays(-daysOld);
            return new MembershipEntry
            {
                SiloAddress = siloAddress,
                HostName = "localhost",
                SiloName = $"TestSilo-{port}",
                Status = status,
                StartTime = now,
                IAmAliveTime = now,
            };
        }
    }
}
