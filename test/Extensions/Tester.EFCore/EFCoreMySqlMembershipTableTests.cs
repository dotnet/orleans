using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Clustering;
using Orleans.Clustering.EntityFrameworkCore.MySql.Data;
using Orleans.Messaging;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;

namespace Tester.EFCore;

[TestCategory("Membership"), TestCategory("EFCore"), TestCategory("EFCore-MySql")]
public class EFCoreMySqlMembershipTableTests : MembershipTableTestsBase
{
    public EFCoreMySqlMembershipTableTests(ConnectionStringFixture fixture, TestEnvironmentFixture environment) : base(fixture, environment, CreateFilters())
    {
        EFCoreTestUtils.CheckMySql();
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(nameof(EFCoreMySqlMembershipTableTests), LogLevel.Trace);
        return filters;
    }

    protected override IMembershipTable CreateMembershipTable(ILogger logger)
    {
        return new EFMembershipTable<MySqlClusterDbContext, DateTime>(this.loggerFactory, this._clusterOptions, this.GetFactory(), new MySqlClusterETagConverter());
    }

    protected override Task<string> GetConnectionString()
    {
        var cs = "Server=localhost;Database=OrleansTests.Membership;Uid=root;Pwd=yourStrong(!)Password;";
        return Task.FromResult(cs);
    }

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
    {
        return new EFGatewayListProvider<MySqlClusterDbContext, DateTime>(this.loggerFactory, this._clusterOptions, this._gatewayOptions, this.GetFactory());
    }

    private IDbContextFactory<MySqlClusterDbContext> GetFactory()
    {
        var sp = new ServiceCollection()
            .AddPooledDbContextFactory<MySqlClusterDbContext>(optionsBuilder =>
            {
                optionsBuilder
                    .LogTo(Console.WriteLine, new[] {DbLoggerCategory.Database.Command.Name}, LogLevel.Information)
                    .EnableSensitiveDataLogging();
                optionsBuilder.UseMySQL(this.connectionString, opt =>
                {
                    opt.MigrationsHistoryTable("__EFMigrationsHistory");
                    opt.MigrationsAssembly(typeof(MySqlClusterDbContext).Assembly.FullName);
                });
            }).BuildServiceProvider();

        var factory = sp.GetRequiredService<IDbContextFactory<MySqlClusterDbContext>>();

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
    public void MembershipTable_MySql_Init()
    {
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_GetGateways()
    {
        await MembershipTable_GetGateways();
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_ReadAll_EmptyTable()
    {
        await MembershipTable_ReadAll_EmptyTable();
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_InsertRow()
    {
        await MembershipTable_InsertRow();
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_ReadRow_Insert_Read()
    {
        await MembershipTable_ReadRow_Insert_Read();
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_ReadAll_Insert_ReadAll()
    {
        await MembershipTable_ReadAll_Insert_ReadAll();
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_UpdateRow()
    {
        await MembershipTable_UpdateRow();
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_UpdateRowInParallel()
    {
        await MembershipTable_UpdateRowInParallel();
    }

    [SkippableFact]
    public async Task MembershipTable_MySql_UpdateIAmAlive()
    {
        await MembershipTable_UpdateIAmAlive();
    }

    [SkippableFact]
    public async Task MembershipTableMySqlSql_CleanupDefunctSiloEntries()
    {
        await MembershipTable_CleanupDefunctSiloEntries();
    }
}