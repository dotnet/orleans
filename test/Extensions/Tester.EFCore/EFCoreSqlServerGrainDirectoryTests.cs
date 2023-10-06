using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using Tester.Directories;
using Xunit.Abstractions;

namespace Tester.EFCore;

[TestCategory("Reminders"), TestCategory("EFCore"), TestCategory("EFCore-SqlServer")]
public class EFCoreSqlServerGrainDirectoryTests : GrainDirectoryTests<EFCoreGrainDirectory<SqlServerGrainDirectoryDbContext, byte[]>>
{
    public EFCoreSqlServerGrainDirectoryTests(ITestOutputHelper testOutputHelper) : base(testOutputHelper)
    {
    }

    protected override EFCoreGrainDirectory<SqlServerGrainDirectoryDbContext, byte[]> GetGrainDirectory()
    {
        EFCoreTestUtils.CheckSqlServer();

        var clusterOptions = new ClusterOptions
        {
            ClusterId = Guid.NewGuid().ToString("N"),
            ServiceId = Guid.NewGuid().ToString("N"),
        };

        var loggerFactory = TestingUtils.CreateDefaultLoggerFactory("EFCoreSqlServerGrainDirectoryTests.log");

        var cs = "Server=localhost,1433;Database=OrleansTests.GrainDirectory;User Id=sa;Password=yourStrong(!)Password;TrustServerCertificate=True";
        var sp = new ServiceCollection()
            .AddPooledDbContextFactory<SqlServerGrainDirectoryDbContext>(optionsBuilder =>
            {
                optionsBuilder.UseSqlServer(cs, opt =>
                {
                    opt.MigrationsHistoryTable("__EFMigrationsHistory");
                    opt.MigrationsAssembly(typeof(SqlServerGrainDirectoryDbContext).Assembly.FullName);
                });
            }).BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<SqlServerGrainDirectoryDbContext>>();

        var ctx = factory.CreateDbContext();
        if (ctx.Database.GetPendingMigrations().Any())
        {
            try
            {
                ctx.Database.Migrate();
            }
            catch { }
        }

        var directory = new EFCoreGrainDirectory<SqlServerGrainDirectoryDbContext, byte[]>(loggerFactory, factory, Options.Create(clusterOptions), new SqlServerGrainDirectoryETagConverter());

        return directory;
    }

    [SkippableFact]
        public async Task UnregisterMany()
        {
            const int N = 25;
            const int R = 4;

            // Create and insert N entries
            var addresses = new List<GrainAddress>();
            for (var i = 0; i < N; i++)
            {
                var addr = new GrainAddress
                {
                    ActivationId = ActivationId.NewId(),
                    GrainId = GrainId.Parse("user/someraondomuser_" + Guid.NewGuid().ToString("N")),
                    SiloAddress = SiloAddress.FromParsableString("10.0.23.12:1000@5678"),
                    MembershipVersion = new MembershipVersion(51)
                };
                addresses.Add(addr);
                await this.grainDirectory.Register(addr, previousAddress: null);
            }

            // Modify the Rth entry locally, to simulate another activation tentative by another silo
            var ra = addresses[R];
            var oldActivation = ra.ActivationId;
            addresses[R] = new()
            {
                GrainId = ra.GrainId,
                SiloAddress = ra.SiloAddress,
                MembershipVersion = ra.MembershipVersion,
                ActivationId = ActivationId.NewId()
            };

            // Batch unregister
            await this.grainDirectory.UnregisterMany(addresses);

            // Now we should only find the old Rth entry
            for (int i = 0; i < N; i++)
            {
                if (i == R)
                {
                    var addr = await this.grainDirectory.Lookup(addresses[i].GrainId);
                    Assert.NotNull(addr);
                    Assert.Equal(oldActivation, addr.ActivationId);
                }
                else
                {
                    Assert.Null(await this.grainDirectory.Lookup(addresses[i].GrainId));
                }
            }
        }
}