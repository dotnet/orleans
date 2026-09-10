#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Xunit;
using AdvancedReminderOptions = Orleans.AdvancedReminders.ReminderOptions;
using AdvancedReminderServiceInterface = Orleans.AdvancedReminders.IReminderService;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderRegistryValidationTests
{
    [Fact]
    public async Task RegisterInterval_RejectsInfiniteDueTime()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsNegativeDueTime()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsInfinitePeriod()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task RegisterInterval_RejectsNegativePeriod()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsZeroPeriodWhenConfiguredMinimumIsZero()
    {
        var registry = CreateRegistry(new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.Zero });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(
                GrainId.Create("test", "g"),
                "r",
                TimeSpan.Zero,
                TimeSpan.Zero));
    }

    [Fact]
    public async Task RegisterInterval_RejectsPeriodBelowMinimum()
    {
        var registry = CreateRegistry(new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(2) });

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterOneShot_AllowsZeroPeriodWithoutLoweringMinimumReminderPeriod()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var reminder = Substitute.For<IGrainReminder>();
        var grainId = GrainId.Create("test", "one-shot");
        var dueAtUtc = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.FireImmediately)
            .Returns(Task.FromResult(reminder));
        var registry = CreateRegistry(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromHours(1) },
            reminderService: service);

        var result = await registry.RegisterOrUpdateReminder(
            grainId,
            "r",
            ReminderSchedule.OneShot(dueAtUtc),
            MissedReminderAction.FireImmediately);

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Interval
                && schedule.IsOneShot
                && schedule.DueAtUtc == dueAtUtc
                && schedule.DueTime == null
                && schedule.Period == TimeSpan.Zero),
            MissedReminderAction.FireImmediately);
    }

    [Fact]
    public void OneShot_WithDateTimeOffset_NormalizesDueTimeToUtc()
    {
        var dueAt = new DateTimeOffset(2030, 4, 15, 19, 30, 0, TimeSpan.FromHours(3));

        var schedule = ReminderSchedule.OneShot(dueAt);

        Assert.Equal(new DateTime(2030, 4, 15, 16, 30, 0, DateTimeKind.Utc), schedule.DueAtUtc);
        Assert.Equal(DateTimeKind.Utc, schedule.DueAtUtc!.Value.Kind);
        Assert.True(schedule.IsOneShot);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void OneShot_WithDateTime_RejectsNonUtcKind(DateTimeKind kind)
    {
        var dueAt = new DateTime(2030, 4, 15, 16, 30, 0, kind);

        var exception = Assert.Throws<ArgumentException>(() => ReminderSchedule.OneShot(dueAt));

        Assert.Equal("dueAtUtc", exception.ParamName);
    }

    [Fact]
    public async Task RegisterInterval_RejectsEmptyName()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsInvalidAction()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2), (MissedReminderAction)255));
    }

    [Fact]
    public async Task RegisterAbsolute_RejectsNonUtcDueTimestamp()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", DateTime.Now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task RegisterCron_RejectsEmptyName()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), " ", "*/5 * * * *"));
    }

    [Fact]
    public async Task RegisterCron_RejectsEmptyExpression()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", " "));
    }

    [Theory]
    [InlineData("invalid cron")]
    [InlineData("70 9 * * *")]
    [InlineData("0 9 * NO_MONTH *")]
    public async Task RegisterCron_RejectsInvalidExpression(string expression)
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAnyAsync<FormatException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", expression));
    }

    [Fact]
    public async Task RegisterCron_RejectsInvalidAction()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", "*/5 * * * *", (MissedReminderAction)255));
    }

    [Fact]
    public async Task RegisterCron_UsesDurableJobsClockForMinimumPeriodValidation()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 7, 13, 59, 0, TimeSpan.Zero));
        var registry = CreateRegistry(
            new AdvancedReminderOptions { MinimumReminderPeriod = TimeSpan.FromHours(23.5) },
            timeProvider: timeProvider);
        var timeZoneId = AdvancedReminderTimeZoneTestHelper.GetUsEasternTimeZone().Id;

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(
                GrainId.Create("test", "g"),
                "r",
                ReminderSchedule.Cron("0 9 * * *", timeZoneId),
                MissedReminderAction.Skip));
    }

    [Fact]
    public async Task Register_WithValidInputAndMissingService_ThrowsInvalidOperation()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task Register_WithValidInput_DelegatesToReminderService()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var reminder = Substitute.For<IGrainReminder>();
        var grainId = GrainId.Create("test", "delegate");
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Is<ReminderSchedule>(schedule =>
                    schedule.Kind == ReminderScheduleKind.Interval
                    && schedule.DueTime == TimeSpan.Zero
                    && schedule.DueAtUtc == null
                    && schedule.Period == TimeSpan.FromMinutes(2)
                    && schedule.CronExpression == null
                    && schedule.CronTimeZoneId == null),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var registry = CreateRegistry(reminderService: service);

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2));

        Assert.Same(reminder, result);
    }

    private static ReminderRegistry CreateRegistry(
        AdvancedReminderOptions? options = null,
        AdvancedReminderServiceInterface? reminderService = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        if (reminderService is not null)
        {
            services.AddSingleton<AdvancedReminderServiceInterface>(reminderService);
        }

        return new ReminderRegistry(
            services.BuildServiceProvider(),
            Options.Create(options ?? new AdvancedReminderOptions()),
            timeProvider ?? TimeProvider.System);
    }
}
