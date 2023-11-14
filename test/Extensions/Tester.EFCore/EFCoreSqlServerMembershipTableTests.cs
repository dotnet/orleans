using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Clustering.EntityFrameworkCore.SqlServer;
using Orleans.Messaging;
using Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;

namespace Tester.EFCore;

[TestCategory("Membership"), TestCategory("EFCore"), TestCategory("EFCore-SqlServer")]
public class EFCoreSqlServerMembershipTableTests : MembershipTableTestsBase
{
    private readonly IEFClusterETagConverter<byte[]> _converter = new SqlServerClusterETagConverter();
    public EFCoreSqlServerMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
    {
        EFCoreTestUtils.CheckSqlServer();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(nameof(EFCoreSqlServerMembershipTableTests), LogLevel.Trace);
        return filters;
    }

    protected override IMembershipTable CreateMembershipTable(ILogger logger)
    {
        return new EFMembershipTable<SqlServerClusterDbContext, byte[]>(this.loggerFactory, this._clusterOptions, this.GetFactory(), this._converter);
    }

    protected override Task<string> GetConnectionString()
    {
        var cs = "Server=localhost,1433;Database=OrleansTests.Membership;User Id=sa;Password=yourStrong(!)Password;TrustServerCertificate=True";
        return Task.FromResult(cs);
    }

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        return new EFGatewayListProvider<SqlServerClusterDbContext, byte[]>(this.loggerFactory, this._clusterOptions, this._gatewayOptions, this.GetFactory());
    }

    private IDbContextFactory<SqlServerClusterDbContext> GetFactory()
    {
        var sp = new ServiceCollection()
            .AddSingleton<IEFClusterETagConverter<byte[]>, SqlServerClusterETagConverter>()
            .AddPooledDbContextFactory<SqlServerClusterDbContext>(optionsBuilder =>
            {
                optionsBuilder.UseSqlServer(this.connectionString, opt =>
                {
                    opt.MigrationsHistoryTable("__EFMigrationsHistory");
                    opt.MigrationsAssembly(typeof(SqlServerClusterDbContext).Assembly.FullName);
                });
            }).BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<SqlServerClusterDbContext>>();

        var ctx = factory.CreateDbContext();
        if (ctx.Database.GetPendingMigrations().Any())
        {
            try
            {
                ctx.Database.Migrate();
            }
            catch { }
        }

        return factory;
    }

    [SkippableFact]
    public void MembershipTable_SqlServer_Init()
    {
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_GetGateways()
    {
        await MembershipTable_GetGateways();
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_ReadAll_EmptyTable()
    {
        await MembershipTable_ReadAll_EmptyTable();
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_InsertRow()
    {
        await MembershipTable_InsertRow();
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_ReadRow_Insert_Read()
    {
        await MembershipTable_ReadRow_Insert_Read();
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_ReadAll_Insert_ReadAll()
    {
        await MembershipTable_ReadAll_Insert_ReadAll();
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_UpdateRow()
    {
        await MembershipTable_UpdateRow();
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_UpdateRowInParallel()
    {
        await MembershipTable_UpdateRowInParallel();
    }

    [SkippableFact]
    public async Task MembershipTable_SqlServer_UpdateIAmAlive()
    {
        await MembershipTable_UpdateIAmAlive();
    }

    [SkippableFact]
    public async Task MembershipTableSqlServerSql_CleanupDefunctSiloEntries()
    {
        await MembershipTable_CleanupDefunctSiloEntries();
    }
}