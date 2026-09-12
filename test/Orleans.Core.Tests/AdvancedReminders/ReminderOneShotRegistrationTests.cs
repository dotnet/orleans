using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Timers;
using Xunit;
using IGrainReminder = Orleans.AdvancedReminders.IGrainReminder;
using IRemindable = Orleans.AdvancedReminders.IRemindable;
using IReminderService = Orleans.AdvancedReminders.IReminderService;

namespace UnitTests.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Reminders")]
public class ReminderOneShotRegistrationTests
{
    public static IEnumerable<object[]> Registrations()
    {
        foreach (var receiver in new[] { "grain", "grain-base", "registry", "service" })
            foreach (var kind in new[] { "delay", "utc", "offset" })
                foreach (var action in Enum.GetValues<MissedReminderAction>())
                {
                    yield return [receiver, kind, action];
                }
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task OneShotOverloads_PreserveIdentityTimingAndMissedPolicy(string receiver, string kind, MissedReminderAction action)
    {
        var id = GrainId.Create("one-shot-test", "target");
        var registry = Substitute.For<IReminderRegistry>();
        var service = Substitute.For<IReminderService>();
        var handle = Substitute.For<IGrainReminder>();
        ReminderSchedule? captured = null;
        registry.RegisterOrUpdateReminder(id, "once", Arg.Do<ReminderSchedule>(value => captured = value), action).Returns(handle);
        service.RegisterOrUpdateReminder(id, "once", Arg.Do<ReminderSchedule>(value => captured = value), action).Returns(handle);
        using var services = new ServiceCollection().AddSingleton(registry).BuildServiceProvider();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(id);
        context.ActivationServices.Returns(services);
        Grain grain = new OneShotGrain(context);
        IGrainBase grainBase = grain;
        var delay = TimeSpan.FromMinutes(17);
        var offset = new DateTimeOffset(2030, 4, 15, 22, 15, 0, TimeSpan.FromMinutes(345));
        var utc = offset.UtcDateTime;

        var result = await ((receiver, kind) switch
        {
            ("grain", "delay") => grain.RegisterOrUpdateAdvancedReminder("once", delay, action),
            ("grain", "utc") => grain.RegisterOrUpdateAdvancedReminder("once", utc, action),
            ("grain", "offset") => grain.RegisterOrUpdateAdvancedReminder("once", offset, action),
            ("grain-base", "delay") => grainBase.RegisterOrUpdateAdvancedReminder("once", delay, action),
            ("grain-base", "utc") => grainBase.RegisterOrUpdateAdvancedReminder("once", utc, action),
            ("grain-base", "offset") => grainBase.RegisterOrUpdateAdvancedReminder("once", offset, action),
            ("registry", "delay") => registry.RegisterOrUpdateReminder(id, "once", delay, action),
            ("registry", "utc") => registry.RegisterOrUpdateReminder(id, "once", utc, action),
            ("registry", "offset") => registry.RegisterOrUpdateReminder(id, "once", offset, action),
            ("service", "delay") => service.RegisterOrUpdateReminder(id, "once", delay, action),
            ("service", "utc") => service.RegisterOrUpdateReminder(id, "once", utc, action),
            ("service", "offset") => service.RegisterOrUpdateReminder(id, "once", offset, action),
            _ => throw new InvalidOperationException(),
        });

        Assert.Same(handle, result);
        Assert.NotNull(captured);
        Assert.True(captured.IsOneShot);
        Assert.Equal(ReminderScheduleKind.Interval, captured.Kind);
        Assert.Equal(TimeSpan.Zero, captured.Period);
        Assert.Equal(kind == "delay" ? delay : (TimeSpan?)null, captured.DueTime);
        Assert.Equal(kind == "delay" ? null : (DateTime?)utc, captured.DueAtUtc);
        Assert.Null(captured.CronExpression);
        Assert.Null(captured.CronTimeZoneId);
        Assert.Single(receiver == "service" ? service.ReceivedCalls() : registry.ReceivedCalls());
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task NonUtcDates_AreRejectedBeforeRegistering(DateTimeKind kind)
    {
        var registry = Substitute.For<IReminderRegistry>();
        var service = Substitute.For<IReminderService>();
        var date = DateTime.SpecifyKind(DateTime.UnixEpoch, kind);
        var id = GrainId.Create("one-shot-test", "invalid");
        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterOrUpdateReminder(id, "once", date));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterOrUpdateReminder(id, "once", date));
        Assert.Empty(registry.ReceivedCalls());
        Assert.Empty(service.ReceivedCalls());
    }

    [Theory]
    [InlineData("delay")]
    [InlineData("utc")]
    [InlineData("offset")]
    public async Task DefaultMissedPolicy_IsSkip(string kind)
    {
        var registry = Substitute.For<IReminderRegistry>();
        var id = GrainId.Create("one-shot-test", "default-policy");
        await (kind switch
        {
            "delay" => registry.RegisterOrUpdateReminder(id, "once", TimeSpan.FromMinutes(1)),
            "utc" => registry.RegisterOrUpdateReminder(id, "once", DateTime.UnixEpoch),
            "offset" => registry.RegisterOrUpdateReminder(id, "once", DateTimeOffset.UnixEpoch),
            _ => throw new InvalidOperationException(kind),
        });

        await registry.Received(1).RegisterOrUpdateReminder(id, "once", Arg.Is<ReminderSchedule>(value => value.IsOneShot), MissedReminderAction.Skip);
    }

    [Fact]
    public async Task NullReceivers_AreRejected()
    {
        var id = GrainId.Create("one-shot-test", "null");
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IReminderRegistry)null!).RegisterOrUpdateReminder(id, "once", TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IReminderRegistry)null!).RegisterOrUpdateReminder(id, "once", DateTime.UnixEpoch));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IReminderRegistry)null!).RegisterOrUpdateReminder(id, "once", DateTimeOffset.UnixEpoch));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IReminderService)null!).RegisterOrUpdateReminder(id, "once", TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IReminderService)null!).RegisterOrUpdateReminder(id, "once", DateTime.UnixEpoch));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IReminderService)null!).RegisterOrUpdateReminder(id, "once", DateTimeOffset.UnixEpoch));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((Grain)null!).RegisterOrUpdateAdvancedReminder("once", DateTimeOffset.UnixEpoch));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IGrainBase)null!).RegisterOrUpdateAdvancedReminder("once", TimeSpan.Zero));
    }

    private sealed class OneShotGrain(IGrainContext context) : Grain(context), IRemindable
    {
        public Task ReceiveReminder(string reminderName, Orleans.AdvancedReminders.Runtime.TickStatus status) => Task.CompletedTask;
    }
}
