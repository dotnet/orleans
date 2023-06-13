using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnitTests;
using TestExtensions;
using UnitTests.RemindersTest;
using Orleans.Reminders.GoogleFirestore;


namespace Orleans.Tests.Google;

[TestCategory("Reminders"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class FirestoreRemindersTests : ReminderTableTestsBase
{
    public FirestoreRemindersTests(ConnectionStringFixture fixture)
        : base(fixture, new TestEnvironmentFixture(), new LoggerFilterOptions())
    {
    }

    protected override IReminderTable CreateRemindersTable()
    {
        GoogleEmulatorHost.Instance.EnsureStarted().GetAwaiter().GetResult();

        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint
        };

        return new GoogleFirestoreReminderTable(
            this.loggerFactory,
            this.clusterOptions,
            Options.Create(options));
    }

    protected override Task<string> GetConnectionString() => Task.FromResult(GoogleEmulatorHost.FirestoreEndpoint);

    [Fact]
    public void Init()
    {
    }

    [Fact]
    public async Task Range()
    {
        await RemindersRange(50);
    }

    [Fact]
    public async Task ParallelUpsert()
    {
        await RemindersParallelUpsert();
    }

    [Fact]
    public async Task Simple()
    {
        await ReminderSimple();
    }
}
