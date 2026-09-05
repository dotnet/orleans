using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using TestExtensions;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("Reminders")]
[TestCategory("EFCore")]
[TestCategory(EFCoreTestCategories.SqlServer)]
public sealed class EFCoreSqlServerReminderStandaloneTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _testOutput;
    private EFCoreDatabaseFixture<SqlServerReminderDbContext>? _databaseFixture;

    public EFCoreSqlServerReminderStandaloneTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    public async Task InitializeAsync()
    {
        _databaseFixture = new EFCoreDatabaseFixture<SqlServerReminderDbContext>(
            EFCoreTestDatabase.SqlServer,
            "reminder_standalone",
            GetTargetFramework(),
            writeOutput: message => _testOutput.WriteLine(message));
        await _databaseFixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_databaseFixture is not null)
        {
            await _databaseFixture.DisposeAsync();
        }
    }

    [SkippableFact, TestCategory("Performance")]
    public async Task Reminders_EFCoreSqlServer_InsertThroughputCompletesAndPersistsEveryRow()
    {
        var table = CreateReminderTable();
        await table.StartAsync();
        var stopwatch = Stopwatch.StartNew();

        var firstBatch = await InsertBatch(table, 0, 10);
        var secondBatch = await InsertBatch(table, firstBatch.Length, 500);

        stopwatch.Stop();
        var allETags = firstBatch.Concat(secondBatch).ToArray();
        Assert.Equal(510, allETags.Length);
        Assert.Equal(510, allETags.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(510, (await table.ReadRows(0, 0)).Reminders.Count);
        _testOutput.WriteLine(
            $"Inserted {allETags.Length} SQL Server EF reminder rows in {stopwatch.Elapsed}.");
    }

    [SkippableFact, TestCategory(EFCoreTestCategories.Functional)]
    public async Task Reminders_EFCoreSqlServer_InsertAndReadBackPreservesFieldsAndReturnsETag()
    {
        var table = CreateReminderTable();
        await table.StartAsync();
        var expected = new ReminderEntry
        {
            GrainId = GrainId.Create("standalone-reminder", $"grain-{Guid.NewGuid():N}"),
            ReminderName = "reminder/#? with spaces",
            Period = TimeSpan.FromSeconds(37),
            StartAt = new DateTime(2026, 8, 11, 12, 13, 14, DateTimeKind.Utc)
        };

        expected.ETag = await table.UpsertRow(expected);
        var actual = Assert.IsType<ReminderEntry>(
            await table.ReadRow(expected.GrainId, expected.ReminderName));

        Assert.Equal(expected.GrainId, actual.GrainId);
        Assert.Equal(expected.ReminderName, actual.ReminderName);
        Assert.Equal(expected.Period, actual.Period);
        Assert.Equal(expected.StartAt, actual.StartAt);
        Assert.Equal(expected.ETag, actual.ETag);
        Assert.False(string.IsNullOrWhiteSpace(actual.ETag));
        Assert.Single((await table.ReadRows(expected.GrainId)).Reminders);
    }

    private IReminderTable CreateReminderTable() =>
        new EFReminderTable<SqlServerReminderDbContext, byte[]>(
            NullLoggerFactory.Instance,
            Options.Create(new ClusterOptions
            {
                ClusterId = $"cluster-{Guid.NewGuid():N}",
                ServiceId = $"service-{Guid.NewGuid():N}"
            }),
            _databaseFixture?.Factory
                ?? throw new InvalidOperationException("The reminder database has not been initialized."),
            new Orleans.Reminders.SqlServerReminderETagConverter());

    private static async Task<string[]> InsertBatch(
        IReminderTable table,
        int offset,
        int count) =>
        await Task.WhenAll(Enumerable.Range(offset, count).Select(async index =>
        {
            var entry = new ReminderEntry
            {
                GrainId = GrainId.Create("standalone-throughput", $"grain-{index:D4}-{Guid.NewGuid():N}"),
                ReminderName = $"reminder-{index:D4}",
                Period = TimeSpan.FromMinutes(3),
                StartAt = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(index)
            };

            var etag = await table.UpsertRow(entry);
            Assert.False(string.IsNullOrWhiteSpace(etag));
            return etag;
        }));

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
