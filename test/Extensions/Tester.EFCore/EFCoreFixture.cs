using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.EntityFrameworkCore.SqlServer;
using Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;
using Orleans.Persistence;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using Orleans.Reminders;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using Orleans.TestingHost;
using TestExtensions;

namespace Tester.EFCore;

public class EFCoreFixture<TDbContext> : BaseTestClusterFixture where TDbContext : DbContext
{
    protected override void CheckPreconditionsOrThrow() => EFCoreTestUtils.CheckSqlServer();

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 4;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            var ctxTypeName = typeof(TDbContext).Name;
            var cs = $"Server=localhost,1433;Database=OrleansTests.{ctxTypeName};User Id=sa;Password=yourStrong(!)Password;TrustServerCertificate=True";

            hostBuilder.Services.AddPooledDbContextFactory<TDbContext>(optionsBuilder =>
            {
                optionsBuilder.UseSqlServer(cs, opt =>
                {
                    opt.MigrationsHistoryTable("__EFMigrationsHistory");
                    opt.MigrationsAssembly(typeof(TDbContext).Assembly.FullName);
                });
            });

            switch (ctxTypeName)
            {
                case nameof(SqlServerClusterDbContext):
                    hostBuilder.Services.AddSingleton<IEFClusterETagConverter<byte[]>, SqlServerClusterETagConverter>();
                    break;
                case nameof(SqlServerGrainStateDbContext):
                    hostBuilder
                        .AddEntityFrameworkCoreSqlServerGrainStorage("GrainStorageForTest");
                    break;
                case nameof(SqlServerReminderDbContext):
                    hostBuilder
                        .UseEntityFrameworkCoreSqlServerReminderService();
                    break;
            }

            hostBuilder
                .AddMemoryGrainStorage("MemoryStore");

            var sp = new ServiceCollection()
                .AddPooledDbContextFactory<TDbContext>(optionsBuilder =>
                {
                    optionsBuilder.UseSqlServer(cs, opt =>
                    {
                        opt.MigrationsHistoryTable("__EFMigrationsHistory");
                        opt.MigrationsAssembly(typeof(TDbContext).Assembly.FullName);
                    });
                }).BuildServiceProvider();

            var factory = sp.GetRequiredService<IDbContextFactory<TDbContext>>();

            var ctx = factory.CreateDbContext();
            if (ctx.Database.GetPendingMigrations().Any())
            {
                try
                {
                    ctx.Database.Migrate();
                }
                catch { }
            }
        }
    }
}