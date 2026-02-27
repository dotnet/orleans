#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Timers;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderRegistrationExtensionsTests
{
    [Fact]
    public async Task RegistryExtension_WithBuilder_DelegatesToStringOverload()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "registry-builder");
        var reminder = Substitute.For<IGrainReminder>();
        registry.RegisterOrUpdateReminder(grainId, "r", "30 9 * * MON-FRI", null).Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(grainId, "r", ReminderCronBuilder.WeekdaysAt(9, 30));

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(grainId, "r", "30 9 * * MON-FRI", null);
    }

    [Fact]
    public async Task RegistryExtension_WithExpressionAndPriority_DelegatesToStringOverload()
    {
        var registry = Substitute.For<IReminderRegistry>();
        var grainId = GrainId.Create("test", "registry-expression-priority");
        var reminder = Substitute.For<IGrainReminder>();
        var expression = ReminderCronExpression.Parse("*/5 * * * *");
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                "*/5 * * * *",
                ReminderPriority.High,
                MissedReminderAction.Notify,
                null)
            .Returns(Task.FromResult(reminder));

        var result = await registry.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            ReminderPriority.High,
            MissedReminderAction.Notify);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "*/5 * * * *",
            ReminderPriority.High,
            MissedReminderAction.Notify,
            null);
    }

    [Fact]
    public async Task ServiceExtension_WithBuilder_DelegatesToStringOverload()
    {
        var service = Substitute.For<IReminderService>();
        var grainId = GrainId.Create("test", "service-builder");
        var reminder = Substitute.For<IGrainReminder>();
        service.RegisterOrUpdateReminder(grainId, "r", "0 7 * * *", null).Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(grainId, "r", ReminderCronBuilder.DailyAt(7, 0));

        Assert.Same(reminder, result);
        await service.Received(1).RegisterOrUpdateReminder(grainId, "r", "0 7 * * *", null);
    }

    [Fact]
    public async Task ServiceExtension_WithExpressionAndPriority_DelegatesToStringOverload()
    {
        var service = Substitute.For<IReminderService>();
        var grainId = GrainId.Create("test", "service-expression-priority");
        var reminder = Substitute.For<IGrainReminder>();
        var expression = ReminderCronExpression.Parse("0 */2 * * * *");
        service.RegisterOrUpdateReminder(
                grainId,
                "r",
                "0 */2 * * * *",
                ReminderPriority.Normal,
                MissedReminderAction.Skip,
                null)
            .Returns(Task.FromResult(reminder));

        var result = await service.RegisterOrUpdateReminder(
            grainId,
            "r",
            expression,
            ReminderPriority.Normal,
            MissedReminderAction.Skip);

        Assert.Same(reminder, result);
        await service.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 */2 * * * *",
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            null);
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
                "15 10 * * *",
                ReminderPriority.Normal,
                MissedReminderAction.Skip,
                null)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder("r", ReminderCronBuilder.DailyAt(10, 15));

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "15 10 * * *",
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            null);
    }

    [Fact]
    public async Task GrainExtension_WithPriorityAndBuilder_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-priority-builder");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                "0 0 * * *",
                ReminderPriority.High,
                MissedReminderAction.FireImmediately,
                null)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder(
            "r",
            ReminderCronBuilder.DailyAt(0, 0),
            ReminderPriority.High,
            MissedReminderAction.FireImmediately);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            "0 0 * * *",
            ReminderPriority.High,
            MissedReminderAction.FireImmediately,
            null);
    }

    [Fact]
    public async Task GrainExtension_WithAbsoluteDueUtc_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-absolute-due");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var dueAtUtc = new DateTime(2026, 2, 1, 10, 5, 0, DateTimeKind.Utc);
        var period = TimeSpan.FromMinutes(2);
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                dueAtUtc,
                period,
                ReminderPriority.Normal,
                MissedReminderAction.Skip)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder("r", dueAtUtc, period);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            dueAtUtc,
            period,
            ReminderPriority.Normal,
            MissedReminderAction.Skip);
    }

    [Fact]
    public async Task GrainExtension_WithPriorityAndAbsoluteDueUtc_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "grain-priority-absolute-due");
        var registry = Substitute.For<IReminderRegistry>();
        var reminder = Substitute.For<IGrainReminder>();
        var dueAtUtc = new DateTime(2026, 2, 1, 11, 0, 0, DateTimeKind.Utc);
        var period = TimeSpan.FromMinutes(1);
        registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                dueAtUtc,
                period,
                ReminderPriority.High,
                MissedReminderAction.FireImmediately)
            .Returns(Task.FromResult(reminder));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.RegisterOrUpdateReminder(
            "r",
            dueAtUtc,
            period,
            ReminderPriority.High,
            MissedReminderAction.FireImmediately);

        Assert.Same(reminder, result);
        await registry.Received(1).RegisterOrUpdateReminder(
            grainId,
            "r",
            dueAtUtc,
            period,
            ReminderPriority.High,
            MissedReminderAction.FireImmediately);
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
            async () => await grain.RegisterOrUpdateReminder("r", "*/5 * * * *"));
        Assert.Contains(nameof(IRemindable), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrainExtension_ThrowsOnNullInputs()
    {
        var grainId = GrainId.Create("test", "null-inputs");
        var registry = Substitute.For<IReminderRegistry>();
        var grain = CreateRemindableGrain(grainId, registry);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await GrainReminderCronExtensions.RegisterOrUpdateReminder((IGrainBase)null!, "r", "*/5 * * * *"));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await grain.RegisterOrUpdateReminder("r", (string)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await grain.RegisterOrUpdateReminder("r", (ReminderCronExpression)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await grain.RegisterOrUpdateReminder("r", (ReminderCronBuilder)null!));
    }

    [Fact]
    public async Task GrainExtension_GetReminders_DelegatesToRegistry()
    {
        var grainId = GrainId.Create("test", "get-reminders");
        var registry = Substitute.For<IReminderRegistry>();
        var expected = new List<IGrainReminder> { Substitute.For<IGrainReminder>() };
        registry.GetReminders(grainId).Returns(Task.FromResult(expected));
        var grain = CreateRemindableGrain(grainId, registry);

        var result = await grain.GetReminders();

        Assert.Same(expected, result);
        await registry.Received(1).GetReminders(grainId);
    }

    [Fact]
    public async Task GrainExtension_GetReminders_ThrowsForNullGrain()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await GrainReminderExtensions.GetReminders((IGrainBase)null!));
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
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((IReminderService)null!, grainId, "r", expression));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ReminderCronRegistrationExtensions.RegisterOrUpdateReminder((IReminderService)null!, grainId, "r", builder));
    }

    private static IGrainBase CreateRemindableGrain(GrainId grainId, IReminderRegistry registry)
    {
        var services = new ServiceCollection().AddSingleton(registry).BuildServiceProvider();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(grainId);
        context.ActivationServices.Returns(services);

        var grain = Substitute.For<IGrainBase, IRemindable>();
        grain.GrainContext.Returns(context);
        return grain;
    }
}
