using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Clustering.EntityFrameworkCore.Data;
using Orleans.Configuration;
using Orleans.Runtime;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Clustering;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class EFCoreMembershipTableDiagnosticsTests
{
    [Fact]
    public async Task ReadRow_MismatchedSuspectorLists_UsesEntityFrameworkCoreDiagnostic()
    {
        await using var fixture = await MembershipFixture.Create();
        var address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 12345);
        await fixture.InsertSilo(
            address,
            suspectingSilos: ["127.0.0.2:11112@12346"],
            suspectingTimes: []);

        var exception = await Assert.ThrowsAsync<WrappedException>(() => fixture.Table.ReadRow(address));
        var diagnostic = exception.ToString();

        Assert.Contains("Entity Framework Core membership record", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Cosmos", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRow_NullSuspectorLists_AreHandledAsEmpty()
    {
        await using var fixture = await MembershipFixture.Create();
        var address = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11112), 12346);
        await fixture.InsertSilo(address, suspectingSilos: [], suspectingTimes: []);
        await using (var context = fixture.Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE Silos SET SuspectingSilos = NULL, SuspectingTimes = NULL");
        }

        var data = await fixture.Table.ReadRow(address);

        var member = Assert.Single(data.Members);
        Assert.Null(member.Item1.SuspectTimes);
    }

    private sealed class MembershipFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _clusterId;

        private MembershipFixture(
            SqliteConnection connection,
            TestDbContextFactory factory,
            string clusterId)
        {
            _connection = connection;
            Factory = factory;
            _clusterId = clusterId;
            Table = new EFMembershipTable<TestClusterDbContext, Guid>(
                NullLoggerFactory.Instance,
                Options.Create(new ClusterOptions { ClusterId = clusterId }),
                factory,
                new GuidClusterETagConverter());
        }

        public TestDbContextFactory Factory { get; }

        public EFMembershipTable<TestClusterDbContext, Guid> Table { get; }

        public static async Task<MembershipFixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TestClusterDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestDbContextFactory(options);
            await using var context = factory.CreateDbContext();
            await context.Database.EnsureCreatedAsync();
            var clusterId = $"diagnostics-{Guid.NewGuid():N}";
            context.Clusters.Add(new ClusterRecord<Guid>
            {
                Id = clusterId,
                Timestamp = DateTimeOffset.UtcNow,
                Version = 0
            });
            await context.SaveChangesAsync();
            return new MembershipFixture(connection, factory, clusterId);
        }

        public async Task InsertSilo(
            SiloAddress address,
            List<string> suspectingSilos,
            List<string> suspectingTimes)
        {
            await using var context = Factory.CreateDbContext();
            context.Silos.Add(new SiloRecord<Guid>
            {
                ClusterId = _clusterId,
                Address = address.Endpoint.Address.ToString(),
                Port = address.Endpoint.Port,
                Generation = address.Generation,
                Name = "diagnostic-silo",
                HostName = "diagnostic-host",
                Status = SiloStatus.Active,
                ProxyPort = 30000,
                SuspectingSilos = suspectingSilos,
                SuspectingTimes = suspectingTimes,
                StartTime = DateTimeOffset.UtcNow,
                IAmAliveTime = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestClusterDbContext> options)
        : IDbContextFactory<TestClusterDbContext>
    {
        public TestClusterDbContext CreateDbContext() => new(options);
    }

    private sealed class TestClusterDbContext(DbContextOptions<TestClusterDbContext> options)
        : GuidClusterDbContext<TestClusterDbContext>(options);
}
