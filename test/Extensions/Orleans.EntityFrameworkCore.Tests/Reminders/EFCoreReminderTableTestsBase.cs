using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.Data;
using TestExtensions;
using UnitTests;
using UnitTests.RemindersTest;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

public abstract class EFCoreReminderTableTestsBase<TDbContext, TETag> :
    ReminderTableTestsBase
    where TDbContext : ReminderDbContext<TDbContext, TETag>
{
    private readonly ITestOutputHelper _testOutput;
    private ServiceProvider? _serviceProvider;
    private IDbContextFactory<TDbContext>? _factory;
    private string? _isolatedConnectionString;

    protected EFCoreReminderTableTestsBase(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment,
        ITestOutputHelper testOutput)
        : base(fixture, environment, CreateFilters())
    {
        _testOutput = testOutput;
    }

    protected abstract EFCoreTestDatabase Database { get; }

    protected abstract IEFReminderETagConverter<TETag> CreateETagConverter();

    protected override IReminderTable CreateRemindersTable() =>
        CreateRemindersTable(clusterOptions.Value.ServiceId);

    protected override Task<string> GetConnectionString()
    {
        _isolatedConnectionString ??= Database.WithDatabase(
            Database.RequireConnectionString(),
            Database.CreateDatabaseName(
                "reminder_table",
                GetTargetFramework()));

        return Task.FromResult(_isolatedConnectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_factory is not null)
            {
                await Database.DeleteDatabaseAsync(
                    _factory,
                    exception => _testOutput.WriteLine(
                        $"Unable to delete isolated {Database.Name} reminder database: {exception.Message}"));
            }

            if (_serviceProvider is not null)
            {
                await _serviceProvider.DisposeAsync();
            }
        }
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public Task RemindersTable_ParallelUpsertsReturnDistinctETags() =>
        RemindersParallelUpsert();

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public Task RemindersTable_ReadUpdateAndConditionalRemove() =>
        ReminderSimple();

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public Task RemindersTable_RangeQueriesIncludeWrapAround() =>
        RemindersRange(iterations: 30);

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task TestOnlyClearTable_ClearsCurrentServiceOnly()
    {
        var primaryGrain = NewGrainId("primary");
        var secondaryGrain = NewGrainId("secondary");
        var primary = CreateReminder(primaryGrain, "primary/reminder", 3);
        var secondary = CreateReminder(secondaryGrain, "secondary/reminder", 7);
        IReminderTable secondaryTable = CreateRemindersTable($"secondary-{Guid.NewGuid():N}");
        await secondaryTable.StartAsync();

        primary.ETag = await RemindersTable.UpsertRow(primary);
        secondary.ETag = await secondaryTable.UpsertRow(secondary);
        await RemindersTable.TestOnlyClearTable();

        Assert.Empty((await RemindersTable.ReadRows(0, 0)).Reminders);
        Assert.Null(await RemindersTable.ReadRow(primary.GrainId, primary.ReminderName));
        var retained = Assert.Single((await secondaryTable.ReadRows(0, 0)).Reminders);
        Assert.Equal(secondary.GrainId, retained.GrainId);
        Assert.Equal(secondary.ReminderName, retained.ReminderName);
        Assert.Equal(secondary.Period, retained.Period);
        Assert.Equal(secondary.ETag, retained.ETag);
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task ReadRows_ByGrain_ReturnsOnlyRequestedGrain()
    {
        var requestedGrain = NewGrainId("requested");
        var otherGrain = NewGrainId("other");
        var first = CreateReminder(requestedGrain, "first/#? reminder", 5);
        var second = CreateReminder(requestedGrain, "second-reminder", 11);
        var other = CreateReminder(otherGrain, "other-reminder", 17);

        first.ETag = await RemindersTable.UpsertRow(first);
        second.ETag = await RemindersTable.UpsertRow(second);
        other.ETag = await RemindersTable.UpsertRow(other);

        var result = await RemindersTable.ReadRows(requestedGrain);

        Assert.Collection(
            result.Reminders.OrderBy(reminder => reminder.ReminderName),
            actual => AssertReminder(first, actual),
            actual => AssertReminder(second, actual));
        Assert.DoesNotContain(result.Reminders, reminder => reminder.GrainId == otherGrain);
        AssertReminder(other, Assert.IsType<ReminderEntry>(
            await RemindersTable.ReadRow(other.GrainId, other.ReminderName)));
    }

    [Fact, TestSuite(EFCoreTestCategories.Functional)]
    public async Task RemoveRow_MissingOrStaleETag_ReturnsFalseAndPreservesWinner()
    {
        var grainId = NewGrainId("conditional-remove");
        var reminder = CreateReminder(grainId, "conditional", 5);
        var staleETag = await RemindersTable.UpsertRow(reminder);
        reminder.ETag = staleETag;
        reminder.Period = TimeSpan.FromHours(25);
        reminder.ETag = await RemindersTable.UpsertRow(reminder);

        Assert.False(await RemindersTable.RemoveRow(grainId, reminder.ReminderName, staleETag!));
        AssertReminder(reminder, Assert.IsType<ReminderEntry>(
            await RemindersTable.ReadRow(grainId, reminder.ReminderName)));
        Assert.False(await RemindersTable.RemoveRow(
            NewGrainId("missing"),
            "not-registered",
            staleETag!));
    }

    [Theory, TestSuite(EFCoreTestCategories.Functional)]
    [InlineData(25)]
    [InlineData(24 * 36)]
    public async Task PeriodsBeyondOneDay_RoundTrip(long hours)
    {
        var reminder = CreateReminder(NewGrainId($"period-{hours}"), "long-period", 1);
        reminder.Period = TimeSpan.FromHours(hours);

        reminder.ETag = await RemindersTable.UpsertRow(reminder);

        AssertReminder(reminder, Assert.IsType<ReminderEntry>(
            await RemindersTable.ReadRow(reminder.GrainId, reminder.ReminderName)));
    }

    private IDbContextFactory<TDbContext> Factory
    {
        get
        {
            if (_factory is not null)
            {
                return _factory;
            }

            _serviceProvider = new ServiceCollection()
                .AddPooledDbContextFactory<TDbContext>(
                    options => Database.ConfigureOptions(
                        options,
                        connectionStringFixture.ConnectionString,
                        typeof(TDbContext).Assembly.GetName().Name!))
                .BuildServiceProvider();
            _factory = _serviceProvider.GetRequiredService<IDbContextFactory<TDbContext>>();
            Database.MigrateAsync(_factory).GetAwaiter().GetResult();
            return _factory;
        }
    }

    private EFReminderTable<TDbContext, TETag> CreateRemindersTable(string serviceId) =>
        new(
            loggerFactory,
            Options.Create(new ClusterOptions
            {
                ClusterId = $"cluster-{Guid.NewGuid():N}",
                ServiceId = serviceId
            }),
            Factory,
            CreateETagConverter());

    private static ReminderEntry CreateReminder(GrainId grainId, string name, int periodMinutes) =>
        new()
        {
            GrainId = grainId,
            ReminderName = name,
            StartAt = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(periodMinutes)
        };

    private static GrainId NewGrainId(string suffix) =>
        GrainId.Create("efcore-reminder-table", $"{suffix}-{Guid.NewGuid():N}");

    private static void AssertReminder(ReminderEntry expected, ReminderEntry actual)
    {
        Assert.Equal(expected.GrainId, actual.GrainId);
        Assert.Equal(expected.ReminderName, actual.ReminderName);
        Assert.Equal(expected.StartAt, actual.StartAt);
        Assert.Equal(expected.Period, actual.Period);
        Assert.Equal(expected.ETag, actual.ETag);
        Assert.False(string.IsNullOrWhiteSpace(actual.ETag));
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(typeof(EFCoreReminderTableTestsBase<TDbContext, TETag>).FullName, LogLevel.Trace);
        return filters;
    }

    private static string GetTargetFramework()
    {
#if NET8_0
        return "net8";
#elif NET10_0
        return "net10";
#else
        return "unknown";
#endif
    }
}
