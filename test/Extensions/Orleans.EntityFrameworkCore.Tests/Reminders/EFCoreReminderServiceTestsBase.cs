using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Reminders.EntityFrameworkCore.Data;
using Orleans.Testing.Reminders;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.TimerTests;

namespace Orleans.EntityFrameworkCore.Tests.Reminders;

public sealed class EFCoreReminderServiceFixture<TDbContext, TETag, TProvider> :
    BaseInProcessTestClusterFixture
    where TDbContext : ReminderDbContext<TDbContext, TETag>
    where TProvider : EFCoreProviderConfiguration<TETag>, new()
{
    private EFCoreDatabaseFixture<TDbContext>? _databaseFixture;
    private ReminderTestClock? _reminderClock;

    internal ReminderTestClock ReminderClock =>
        _reminderClock
        ?? throw new InvalidOperationException($"{nameof(ReminderTestClock)} has not been configured.");

    protected override void CheckPreconditionsOrThrow() =>
        new TProvider().Database.RequireConnectionString();

    public override async Task InitializeAsync()
    {
        EnsurePreconditionsMet();

        var provider = new TProvider();
        _databaseFixture = new EFCoreDatabaseFixture<TDbContext>(
            provider.Database,
            "reminder_cluster",
            GetTargetFramework(),
            writeOutput: message => Trace.WriteLine(message));

        try
        {
            await _databaseFixture.InitializeAsync();
            await base.InitializeAsync();
        }
        catch
        {
            await _databaseFixture.DisposeAsync();
            throw;
        }
    }

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        var provider = new TProvider();
        var connectionString = _databaseFixture?.ConnectionString
            ?? throw new InvalidOperationException("The reminder database has not been initialized.");

        _reminderClock = builder.AddReminderTestClock();
        builder.ConfigureSilo((_, siloBuilder) =>
            provider.UseReminderService(
                siloBuilder,
                options => provider.Database.ConfigureOptions(
                    options,
                    connectionString,
                    typeof(TDbContext).Assembly.GetName().Name!)));
    }

    public override async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _reminderClock?.Dispose();
            if (_databaseFixture is not null)
            {
                await _databaseFixture.DisposeAsync();
            }
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

public abstract class EFCoreReminderServiceTestsBase<TDbContext, TETag, TProvider> :
    ReminderTestsBase,
    IClassFixture<EFCoreReminderServiceFixture<TDbContext, TETag, TProvider>>
    where TDbContext : ReminderDbContext<TDbContext, TETag>
    where TProvider : EFCoreProviderConfiguration<TETag>, new()
{
    protected EFCoreReminderServiceTestsBase(
        EFCoreReminderServiceFixture<TDbContext, TETag, TProvider> fixture)
        : base(GetReminderClock(fixture), fixture.HostedCluster)
    {
    }

    [SkippableFact, TestCategory(EFCoreTestCategories.Functional)]
    public Task BasicStopByReference() =>
        Test_Reminders_Basic_StopByRef();

    [SkippableFact, TestCategory(EFCoreTestCategories.Functional)]
    public Task UpdateDoesNotRestartLocalReminder() =>
        Test_Reminders_UpdateReminder_DoesNotRestartLocalReminder();

    [SkippableFact, TestCategory(EFCoreTestCategories.Functional)]
    public Task BasicListOperations() =>
        Test_Reminders_Basic_ListOps();

    [SkippableFact, TestCategory(EFCoreTestCategories.Functional)]
    public Task MultipleGrainsAndReminders() =>
        Test_Reminders_1J_MultiGrainMultiReminders();

    [SkippableFact, TestCategory(EFCoreTestCategories.Functional)]
    public Task ReminderNotFound() =>
        Test_Reminders_ReminderNotFound();

    private static ReminderTestClock GetReminderClock(
        EFCoreReminderServiceFixture<TDbContext, TETag, TProvider> fixture)
    {
        fixture.EnsurePreconditionsMet();
        return fixture.ReminderClock;
    }
}
