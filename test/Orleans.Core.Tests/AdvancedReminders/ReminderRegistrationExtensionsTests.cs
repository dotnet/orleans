#nullable enable
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Timers;
using Xunit;
using AdvancedRemindable = Orleans.AdvancedReminders.IRemindable;
using AdvancedReminderServiceInterface = Orleans.AdvancedReminders.IReminderService;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderRegistrationExtensionsTests
{
    [Fact]
    public async Task RegistryExtension_WithBuilder_DelegatesToScheduleMethod()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "registry-builder");
        var reminder = Substitute.For<IGrainReminder>();
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", ReminderCronBuilder.WeekdaysAt(9, 30));

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "30 9 * * MON-FRI"
                && schedule.CronTimeZoneId == null),
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task RegistryExtension_WithExpressionAndAction_DelegatesToScheduleMethod()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "registry-expression-action");
        var reminder = Substitute.For<IGrainReminder>();
        var expression = ReminderCronExpression.Parse("*/5 * * * *");
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Notify)
            .Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            MissedReminderAction.Notify);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "*/5 * * * *"
                && schedule.CronTimeZoneId == null),
            MissedReminderAction.Notify);
    }

    [Fact]
    public async Task ServiceExtension_WithBuilder_DelegatesToScheduleMethod()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var grainId = GrainId.Create("test", "service-builder");
        var reminder = Substitute.For<IGrainReminder>();
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(grainId, "r", ReminderCronBuilder.DailyAt(7, 0));

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 7 * * *"
                && schedule.CronTimeZoneId == null),
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task ServiceExtension_WithExpressionAndAction_DelegatesToScheduleMethod()
    {
        var service = Substitute.For<AdvancedReminderServiceInterface>();
        var grainId = GrainId.Create("test", "service-expression-action");
        var reminder = Substitute.For<IGrainReminder>();
        var expression = ReminderCronExpression.Parse("0 */2 * * * *");
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            MissedReminderAction.Skip);

        Assert.Same(reminder, result);
        _ = service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "0 */2 * * * *"
                && schedule.CronTimeZoneId == null),
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task GrainExtension_WithBuilder_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-builder");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder("r", ReminderCronBuilder.DailyAt(10, 15));

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Cron
                && schedule.CronExpression == "15 10 * * *"
                && schedule.CronTimeZoneId == null),
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task GrainExtension_WithActionAndAbsoluteDueUtc_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-action-absolute-due");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var dueAtUtc = new DateTime(2026, 2, 1, 11, 0, 0, DateTimeKind.Utc);
        var period = TimeSpan.FromMinutes(1);
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.FireImmediately)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder(
            "r",
            dueAtUtc,
            period,
            MissedReminderAction.FireImmediately);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(schedule =>
                schedule.Kind == ReminderScheduleKind.Interval
                && schedule.DueAtUtc == dueAtUtc
                && schedule.DueTime == null
                && schedule.Period == period
                && schedule.CronExpression == null
                && schedule.CronTimeZoneId == null),
            MissedReminderAction.FireImmediately);
    }

    [Fact]
    public async Task GrainExtension_WithSchedule_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-schedule");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var schedule = ReminderSchedule.Cron("15 10 * * *", "Europe/Paris");
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder("r", schedule);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(value => ReferenceEquals(value, schedule)),
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task GrainExtension_WithScheduleAndAction_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-schedule-action");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var schedule = ReminderSchedule.Interval(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                Arg.Any<ReminderSchedule>(),
                MissedReminderAction.Notify)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateAdvancedReminder("r", schedule, MissedReminderAction.Notify);

        Assert.Same(reminder, result);
        _ = registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            Arg.Is<ReminderSchedule>(value => ReferenceEquals(value, schedule)),
            MissedReminderAction.Notify);
    }

    [Fact]
    public async Task GrainExtension_ThrowsWhenGrainIsNotRemindable()
    {
        var grainId = GrainId.Create("test", "non-remindable");
        var registry = Substitute.For<IReminderRegistry>();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.ActivationServices.Returns(new ServiceCollection().AddSingleton(registry).BuildServiceProvider());

        var grain = Substitute.For<IGrainBase>();
        grain.GrainContext.Returns(context);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await grain.RegisterOrUpdateAdvancedReminder("r", "*/5 * * * *"));
        Assert.Contains(typeof(AdvancedRemindable).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationExtensions_ThrowOnNullDependencies()
    {
        var grainId = GrainId.Create("test", "null-dependencies");
        var expression = ReminderCronExpression.Parse("* * * * *");
        var builder = ReminderCronBuilder.EveryMinute();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((IReminderRegistry)null!, grainId, "r", expression));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((IReminderRegistry)null!, grainId, "r", builder));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((AdvancedReminderServiceInterface)null!, grainId, "r", expression));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((AdvancedReminderServiceInterface)null!, grainId, "r", builder));
    }

    private static IGrainBase CreateRemindableGrain(GrainId grainId, IReminderRegistry registry)
    {
        var services = new ServiceCollection().AddSingleton(registry).BuildServiceProvider();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.ActivationServices.Returns(services);

        var grain = Substitute.For<IGrainBase, AdvancedRemindable>();
        grain.GrainContext.Returns(context);
        return grain;
    }
}
