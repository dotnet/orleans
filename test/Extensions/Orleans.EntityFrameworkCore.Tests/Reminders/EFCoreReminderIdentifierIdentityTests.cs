using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.Data;
using Orleans.Reminders.EntityFrameworkCore.MySql.Data;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using Orleans.Runtime;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.MySqlProvider)]
[TestArea("Reminders")]
public sealed class MySqlReminderIdentifierIdentityTests
{
    private readonly ITestOutputHelper _testOutput;

    public MySqlReminderIdentifierIdentityTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public Task PR8654_Reminder_TrailingSpaceIdentifiersRemainDistinct() =>
        ReminderIdentifierIdentityTest.Run<
            MySqlReminderDbContext,
            Guid,
            MySqlEFCoreProviderConfiguration>(_testOutput);
}

[TestArea("EFCore")]
[TestSuite("Functional")]
[TestProvider(EFCoreTestCategories.SqlServer)]
[TestArea("Reminders")]
public sealed class SqlServerReminderIdentifierIdentityTests
{
    private readonly ITestOutputHelper _testOutput;

    public SqlServerReminderIdentifierIdentityTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public Task PR8654_Reminder_TrailingSpaceIdentifiersRemainDistinct() =>
        ReminderIdentifierIdentityTest.Run<
            SqlServerReminderDbContext,
            byte[],
            SqlServerEFCoreProviderConfiguration>(_testOutput);
}

internal static class ReminderIdentifierIdentityTest
{
    public static async Task Run<TDbContext, TETag, TProvider>(ITestOutputHelper testOutput)
        where TDbContext : ReminderDbContext<TDbContext, TETag>
        where TProvider : EFCoreProviderConfiguration<TETag>, new()
    {
        var provider = new TProvider();
        await using var databaseFixture = new EFCoreDatabaseFixture<TDbContext>(
            provider.Database,
            "reminder_identity",
            $"{typeof(TDbContext).Name}_{GetTargetFramework()}",
            writeOutput: testOutput.WriteLine);
        await databaseFixture.InitializeAsync();

        var serviceId = $"identity-{Guid.NewGuid():N}";
        var eTagConverter = provider.CreateReminderETagConverter();
        var table = new EFReminderTable<TDbContext, TETag>(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions
            {
                ClusterId = $"identity-{Guid.NewGuid():N}",
                ServiceId = serviceId
            }),
            databaseFixture.Factory,
            eTagConverter);
        await table.Init();

        const string unspacedIdentifier = "reminder-identity/reminder-key";
        const string spacedIdentifier = "reminder-identity/reminder-key ";
        const string reminderName = "shared-reminder";
        var unspacedId = GrainId.Parse(unspacedIdentifier);
        var spacedId = GrainId.Parse(spacedIdentifier);
        Assert.NotEqual(unspacedId, spacedId);
        Assert.Equal(unspacedIdentifier, unspacedId.ToString());
        Assert.Equal(spacedIdentifier, spacedId.ToString());

        var unspaced = CreateReminder(
            unspacedId,
            reminderName,
            new DateTime(2026, 8, 23, 2, 10, 11, DateTimeKind.Utc),
            TimeSpan.FromMinutes(101));
        var spaced = CreateReminder(
            spacedId,
            reminderName,
            new DateTime(2026, 8, 23, 3, 20, 22, DateTimeKind.Utc),
            TimeSpan.FromMinutes(202));

        unspaced.ETag = await table.UpsertRow(unspaced);
        spaced.ETag = await table.UpsertRow(spaced);

        Assert.False(string.IsNullOrWhiteSpace(unspaced.ETag));
        Assert.False(string.IsNullOrWhiteSpace(spaced.ETag));
        Assert.NotEqual(unspaced.ETag, spaced.ETag);
        await AssertRows(
            databaseFixture.Factory,
            eTagConverter,
            serviceId,
            (unspacedIdentifier, unspaced),
            (spacedIdentifier, spaced));
        AssertReminder(unspaced, await table.ReadRow(unspacedId, unspaced.ReminderName));
        AssertReminder(spaced, await table.ReadRow(spacedId, spaced.ReminderName));
        AssertGrainRows(unspaced, await table.ReadRows(unspacedId));
        AssertGrainRows(spaced, await table.ReadRows(spacedId));

        var unspacedInsertedETag = unspaced.ETag;
        var spacedInsertedETag = spaced.ETag;
        unspaced.StartAt = new DateTime(2026, 8, 24, 4, 30, 33, DateTimeKind.Utc);
        unspaced.Period = TimeSpan.FromMinutes(303);
        spaced.StartAt = new DateTime(2026, 8, 25, 5, 40, 44, DateTimeKind.Utc);
        spaced.Period = TimeSpan.FromMinutes(404);

        unspaced.ETag = await table.UpsertRow(unspaced);
        spaced.ETag = await table.UpsertRow(spaced);

        Assert.NotEqual(unspacedInsertedETag, unspaced.ETag);
        Assert.NotEqual(spacedInsertedETag, spaced.ETag);
        Assert.NotEqual(unspaced.ETag, spaced.ETag);
        AssertReminder(unspaced, await table.ReadRow(unspacedId, unspaced.ReminderName));
        AssertReminder(spaced, await table.ReadRow(spacedId, spaced.ReminderName));
        AssertGrainRows(unspaced, await table.ReadRows(unspacedId));
        AssertGrainRows(spaced, await table.ReadRows(spacedId));
        await AssertRows(
            databaseFixture.Factory,
            eTagConverter,
            serviceId,
            (unspacedIdentifier, unspaced),
            (spacedIdentifier, spaced));

        Assert.True(await table.RemoveRow(unspacedId, unspaced.ReminderName, unspaced.ETag!));

        Assert.Null(await table.ReadRow(unspacedId, unspaced.ReminderName));
        AssertReminder(spaced, await table.ReadRow(spacedId, spaced.ReminderName));
        await AssertRows(
            databaseFixture.Factory,
            eTagConverter,
            serviceId,
            (spacedIdentifier, spaced));

        Assert.True(await table.RemoveRow(spacedId, spaced.ReminderName, spaced.ETag!));

        Assert.Null(await table.ReadRow(spacedId, spaced.ReminderName));
        await using var verification = await databaseFixture.Factory.CreateDbContextAsync();
        Assert.Empty(await verification.Reminders.AsNoTracking().ToListAsync());
    }

    private static ReminderEntry CreateReminder(
        GrainId grainId,
        string name,
        DateTime startAt,
        TimeSpan period) =>
        new()
        {
            GrainId = grainId,
            ReminderName = name,
            StartAt = startAt,
            Period = period
        };

    private static void AssertReminder(ReminderEntry expected, ReminderEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.GrainId, actual.GrainId);
        Assert.Equal(expected.ReminderName, actual.ReminderName);
        Assert.Equal(expected.StartAt, actual.StartAt);
        Assert.Equal(expected.Period, actual.Period);
        Assert.Equal(expected.ETag, actual.ETag);
    }

    private static void AssertGrainRows(ReminderEntry expected, ReminderTableData actual) =>
        AssertReminder(expected, Assert.Single(actual.Reminders));

    private static async Task AssertRows<TDbContext, TETag>(
        IDbContextFactory<TDbContext> factory,
        IEFReminderETagConverter<TETag> eTagConverter,
        string serviceId,
        params (string GrainId, ReminderEntry Reminder)[] expected)
        where TDbContext : ReminderDbContext<TDbContext, TETag>
    {
        await using var context = await factory.CreateDbContextAsync();
        var actual = await context.Reminders.AsNoTracking().ToListAsync();
        Assert.Equal(expected.Length, actual.Count);

        foreach (var (grainId, reminder) in expected)
        {
            var record = Assert.Single(actual, candidate =>
                string.Equals(candidate.GrainId, grainId, StringComparison.Ordinal));
            Assert.Equal(serviceId, record.ServiceId);
            Assert.Equal(grainId, record.GrainId);
            Assert.Equal(reminder.ReminderName, record.Name);
            Assert.Equal(reminder.StartAt, record.StartAt.UtcDateTime);
            Assert.Equal(reminder.Period, record.Period);
            Assert.Equal(reminder.GrainId.GetUniformHashCode(), record.GrainHash);
            Assert.Equal(reminder.ETag, eTagConverter.FromDbETag(record.ETag));
        }
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
