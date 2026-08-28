using Orleans.Hosting;
using Orleans.Tests.SqlUtils;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.TimerTests;

namespace Tester.AdoNet.Reminders;

public abstract class AdoNetReminderServiceLifecycleFixture : BaseInProcessTestClusterFixture
{
    private string _connectionString = null!;
    private ReminderTestClock? _clock;

    protected abstract string Invariant { get; }

    protected abstract string DatabaseName { get; }

    public ReminderTestClock Clock
    {
        get
        {
            EnsurePreconditionsMet();
            return _clock ?? throw new InvalidOperationException("The reminder clock has not been configured.");
        }
    }

    protected override void CheckPreconditionsOrThrow()
        => UnitTests.General.RelationalStorageForTesting.CheckPreconditionsOrThrow(Invariant);

    public override async ValueTask InitializeAsync()
    {
        if (!PreconditionsMet)
        {
            return;
        }

        var relationalStorage = await UnitTests.General.RelationalStorageForTesting.SetupInstance(
            Invariant,
            DatabaseName,
            cancellationToken: TestContext.Current.CancellationToken);
        _connectionString = relationalStorage.CurrentConnectionString;
        await base.InitializeAsync();
    }

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        _clock = builder.AddReminderTestClock();
        builder.ConfigureSilo((_, siloBuilder) =>
            siloBuilder.UseAdoNetReminderService(options =>
            {
                options.ConnectionString = _connectionString;
                options.Invariant = Invariant;
            }));
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _clock?.Dispose();
        }
    }
}

public sealed class PostgreSqlReminderServiceLifecycleFixture : AdoNetReminderServiceLifecycleFixture
{
    protected override string Invariant => AdoNetInvariants.InvariantNamePostgreSql;

    protected override string DatabaseName => "OrleansTest_PostgreSql_ReminderLifecycle";
}

public sealed class MySqlReminderServiceLifecycleFixture : AdoNetReminderServiceLifecycleFixture
{
    protected override string Invariant => AdoNetInvariants.InvariantNameMySql;

    protected override string DatabaseName => "OrleansTest_MySql_ReminderLifecycle";
}

[TestSuite("Functional")]
[TestProvider("PostgreSql")]
[TestArea("Reminders")]
[TestCategory("Functional"), TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("PostgreSql")]
public sealed class PostgreSqlReminderServiceLifecycleTests
    : ReminderServiceLifecycleTestsBase, IClassFixture<PostgreSqlReminderServiceLifecycleFixture>
{
    public PostgreSqlReminderServiceLifecycleTests(PostgreSqlReminderServiceLifecycleFixture fixture)
        : base(fixture.Clock, fixture.HostedCluster, "AdoNet.PostgreSql")
    {
        fixture.EnsurePreconditionsMet();
    }
}

[TestSuite("Functional")]
[TestProvider("MySql")]
[TestArea("Reminders")]
[TestCategory("Functional"), TestCategory("Reminders"), TestCategory("AdoNet"), TestCategory("MySql")]
public sealed class MySqlReminderServiceLifecycleTests
    : ReminderServiceLifecycleTestsBase, IClassFixture<MySqlReminderServiceLifecycleFixture>
{
    public MySqlReminderServiceLifecycleTests(MySqlReminderServiceLifecycleFixture fixture)
        : base(fixture.Clock, fixture.HostedCluster, "AdoNet.MySql")
    {
        fixture.EnsurePreconditionsMet();
    }
}
