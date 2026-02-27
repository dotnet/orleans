#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.ReminderService;
using Xunit;

namespace NonSilo.Tests.Reminders;

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
    public async Task RegisterInterval_RejectsPeriodBelowMinimum()
    {
        var registry = CreateRegistry(new ReminderOptions { MinimumReminderPeriod = TimeSpan.FromMinutes(2) });

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsEmptyName()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "", TimeSpan.Zero, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RegisterInterval_RejectsInvalidPriorityOrAction()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2), (ReminderPriority)255, MissedReminderAction.Skip));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", TimeSpan.Zero, TimeSpan.FromMinutes(2), ReminderPriority.Normal, (MissedReminderAction)255));
    }

    [Fact]
    public async Task RegisterAbsolute_RejectsNonUtcDueTimestamp()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", DateTime.Now, TimeSpan.FromMinutes(2)));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await registry.RegisterOrUpdateReminder(
                grainId,
                "r",
                new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Unspecified),
                TimeSpan.FromMinutes(2),
                ReminderPriority.Normal,
                MissedReminderAction.Skip));
    }

    [Fact]
    public async Task RegisterAbsolute_RejectsInvalidPriorityOrAction()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");
        var dueAtUtc = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", dueAtUtc, TimeSpan.FromMinutes(2), (ReminderPriority)255, MissedReminderAction.Skip));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", dueAtUtc, TimeSpan.FromMinutes(2), ReminderPriority.Normal, (MissedReminderAction)255));
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

    [Fact]
    public async Task RegisterCron_RejectsInvalidExpression()
    {
        var registry = CreateRegistry();

        await Assert.ThrowsAnyAsync<FormatException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", "invalid cron"));
    }

    [Fact]
    public async Task RegisterCron_RejectsInvalidPriorityOrAction()
    {
        var registry = CreateRegistry();
        var grainId = GrainId.Create("test", "g");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", "*/5 * * * *", (ReminderPriority)255, MissedReminderAction.Skip));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await registry.RegisterOrUpdateReminder(grainId, "r", "*/5 * * * *", ReminderPriority.Normal, (MissedReminderAction)255));
    }

    [Fact]
    public async Task RegisterInterval_WithValidInputOutsideGrainContext_ThrowsInvalidOperation()
    {
        var registry = CreateRegistry();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", TimeSpan.Zero, TimeSpan.FromMinutes(2)));

        Assert.Contains("non-grain context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterCron_WithValidInputOutsideGrainContext_ThrowsInvalidOperation()
    {
        var registry = CreateRegistry();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", "*/5 * * * *"));

        Assert.Contains("non-grain context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterAbsolute_WithValidInputOutsideGrainContext_ThrowsInvalidOperation()
    {
        var registry = CreateRegistry();
        var dueAtUtc = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registry.RegisterOrUpdateReminder(GrainId.Create("test", "g"), "r", dueAtUtc, TimeSpan.FromMinutes(2)));

        Assert.Contains("non-grain context", exception.Message, StringComparison.Ordinal);
    }

    private static ReminderRegistry CreateRegistry(ReminderOptions? options = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IInternalGrainFactory>())
            .AddSingleton(Substitute.For<IConsistentRingProvider>())
            .BuildServiceProvider();

        return new ReminderRegistry(services, Options.Create(options ?? new ReminderOptions()));
    }
}
