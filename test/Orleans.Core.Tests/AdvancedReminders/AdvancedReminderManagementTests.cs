#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Cron.Internal;
using Orleans.AdvancedReminders.Runtime;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderTableData = Orleans.AdvancedReminders.ReminderTableData;
using AdvancedReminderException = Orleans.AdvancedReminders.Runtime.ReminderException;

namespace UnitTests.AdvancedReminders;

[TestCategory("Reminders")]
public class ReminderIteratorTests
{
    [Fact]
    public async Task EnumerateAllAsync_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListAllAsync(2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1"), CreateReminder("r2")],
            ContinuationToken = "next",
        }));
        managementGrain.ListAllAsync(2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r3")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateAllAsync(pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2", "r3"], names);
    }

    [Fact]
    public async Task EnumerateFilteredAsync_ReadsAllPages()
    {
        var filter = new ReminderQueryFilter
        {
            Status = ReminderQueryStatus.Overdue | ReminderQueryStatus.Missed,
            OverdueBy = TimeSpan.FromMinutes(2),
            MissedBy = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.High,
        };

        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListFilteredAsync(filter, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListFilteredAsync(filter, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateFilteredAsync(filter, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public async Task EnumerateOverdueAsync_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(3), 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(3), 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateOverdueAsync(TimeSpan.FromMinutes(3), pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public async Task EnumerateDueInRangeAsync_ReadsAllPages()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListDueInRangeAsync(from, to, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListDueInRangeAsync(from, to, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var iterator = new ReminderIterator(managementGrain);
        var names = new List<string>();
        await foreach (var reminder in iterator.EnumerateDueInRangeAsync(from, to, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public void Ctor_NullManagementGrain_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new ReminderIterator(null!));
    }

    private static ReminderEntry CreateReminder(string reminderName)
        => new()
        {
            GrainId = GrainId.Create("test", reminderName),
            ReminderName = reminderName,
            StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(1),
        };
}

[TestCategory("Reminders")]
public class ReminderManagementGrainExtensionsTests
{
    [Fact]
    public async Task EnumerateAllAsync_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListAllAsync(2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1"), CreateReminder("r2")],
            ContinuationToken = "next",
        }));
        managementGrain.ListAllAsync(2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r3")],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateAllAsync(pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2", "r3"], names);
        await managementGrain.Received(1).ListAllAsync(2, null);
        await managementGrain.Received(1).ListAllAsync(2, "next");
    }

    [Fact]
    public void CreateIterator_ReturnsIteratorFacade()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();

        var iterator = managementGrain.CreateIterator();

        Assert.NotNull(iterator);
        Assert.IsType<ReminderIterator>(iterator);
    }

    [Fact]
    public async Task EnumerateOverdueAsync_Extension_ReadsAllPages()
    {
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(2), 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListOverdueAsync(TimeSpan.FromMinutes(2), 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateOverdueAsync(TimeSpan.FromMinutes(2), pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    [Fact]
    public async Task EnumerateDueInRangeAsync_Extension_ReadsAllPages()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var managementGrain = Substitute.For<IReminderManagementGrain>();
        managementGrain.ListDueInRangeAsync(from, to, 2, null).Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r1")],
            ContinuationToken = "next",
        }));
        managementGrain.ListDueInRangeAsync(from, to, 2, "next").Returns(Task.FromResult(new ReminderManagementPage
        {
            Reminders = [CreateReminder("r2")],
            ContinuationToken = null,
        }));

        var names = new List<string>();
        await foreach (var reminder in managementGrain.EnumerateDueInRangeAsync(from, to, pageSize: 2))
        {
            names.Add(reminder.ReminderName);
        }

        Assert.Equal(["r1", "r2"], names);
    }

    private static ReminderEntry CreateReminder(string reminderName)
        => new()
        {
            GrainId = GrainId.Create("test", reminderName),
            ReminderName = reminderName,
            StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(1),
        };
}

[TestCategory("Reminders")]
public class ReminderManagementGrainTests
{
    [Fact]
    public async Task ListAllAsync_ReturnsSortedPagesWithContinuationToken()
    {
        var due = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var table = new InMemoryManagementReminderTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g2"),
                ReminderName = "r2",
                StartAt = due,
                NextDueUtc = due,
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g1"),
                ReminderName = "rB",
                StartAt = due,
                NextDueUtc = due,
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g1"),
                ReminderName = "rA",
                StartAt = due,
                NextDueUtc = due,
                Period = TimeSpan.FromMinutes(1),
            });

        var grain = new ReminderManagementGrain(table);

        var first = await grain.ListAllAsync(pageSize: 2);
        Assert.Equal(["rA", "rB"], first.Reminders.Select(reminder => reminder.ReminderName).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(first.ContinuationToken));

        var second = await grain.ListAllAsync(pageSize: 2, continuationToken: first.ContinuationToken);
        Assert.Single(second.Reminders);
        Assert.Equal("r2", second.Reminders[0].ReminderName);
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task ListFilteredAsync_AppliesPriorityScheduleAndStatus()
    {
        var now = DateTime.UtcNow;
        var table = new InMemoryManagementReminderTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "match"),
                ReminderName = "match",
                StartAt = now.AddMinutes(-20),
                Period = TimeSpan.Zero,
                CronExpression = "*/5 * * * * *",
                NextDueUtc = now.AddMinutes(-10),
                LastFireUtc = now.AddMinutes(-20),
                Priority = ReminderPriority.High,
                Action = MissedReminderAction.FireImmediately,
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "wrong-priority"),
                ReminderName = "wrong-priority",
                StartAt = now.AddMinutes(-20),
                Period = TimeSpan.Zero,
                CronExpression = "*/5 * * * * *",
                NextDueUtc = now.AddMinutes(-10),
                LastFireUtc = null,
                Priority = ReminderPriority.Normal,
                Action = MissedReminderAction.FireImmediately,
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "upcoming"),
                ReminderName = "upcoming",
                StartAt = now.AddMinutes(2),
                Period = TimeSpan.FromMinutes(1),
                NextDueUtc = now.AddMinutes(2),
                Priority = ReminderPriority.High,
                Action = MissedReminderAction.FireImmediately,
            });

        var grain = new ReminderManagementGrain(table);
        var filter = new ReminderQueryFilter
        {
            Status = ReminderQueryStatus.Overdue | ReminderQueryStatus.Missed,
            OverdueBy = TimeSpan.FromMinutes(2),
            MissedBy = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.FireImmediately,
            ScheduleKind = ReminderScheduleKind.Cron,
        };

        var page = await grain.ListFilteredAsync(filter, pageSize: 10);

        Assert.Single(page.Reminders);
        Assert.Equal("match", page.Reminders[0].ReminderName);
    }

    [Fact]
    public async Task ListFilteredAsync_StatusFilters_DistinguishDueUpcomingOverdueAndMissed()
    {
        var now = DateTime.UtcNow;
        var table = new InMemoryManagementReminderTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "due"),
                ReminderName = "due",
                StartAt = now.AddMinutes(-1),
                NextDueUtc = now.AddSeconds(-10),
                Period = TimeSpan.FromMinutes(1),
                LastFireUtc = now,
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "upcoming"),
                ReminderName = "upcoming",
                StartAt = now.AddMinutes(10),
                NextDueUtc = now.AddMinutes(10),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "overdue"),
                ReminderName = "overdue",
                StartAt = now.AddMinutes(-10),
                NextDueUtc = now.AddMinutes(-6),
                Period = TimeSpan.FromMinutes(1),
                LastFireUtc = now.AddMinutes(-1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "missed"),
                ReminderName = "missed",
                StartAt = now.AddMinutes(-10),
                NextDueUtc = now.AddMinutes(-6),
                Period = TimeSpan.FromMinutes(1),
                LastFireUtc = null,
            });
        var grain = new ReminderManagementGrain(table);

        var due = await grain.ListFilteredAsync(new ReminderQueryFilter { Status = ReminderQueryStatus.Due }, pageSize: 10);
        var upcoming = await grain.ListFilteredAsync(new ReminderQueryFilter { Status = ReminderQueryStatus.Upcoming }, pageSize: 10);
        var overdue = await grain.ListFilteredAsync(new ReminderQueryFilter { Status = ReminderQueryStatus.Overdue, OverdueBy = TimeSpan.FromMinutes(5) }, pageSize: 10);
        var missed = await grain.ListFilteredAsync(new ReminderQueryFilter { Status = ReminderQueryStatus.Missed, MissedBy = TimeSpan.FromMinutes(5) }, pageSize: 10);

        Assert.Equal(["missed", "overdue", "due"], due.Reminders.Select(x => x.ReminderName).ToArray());
        Assert.Equal(["upcoming"], upcoming.Reminders.Select(x => x.ReminderName).ToArray());
        Assert.Equal(["missed", "overdue"], overdue.Reminders.Select(x => x.ReminderName).ToArray());
        Assert.Equal(["missed"], missed.Reminders.Select(x => x.ReminderName).ToArray());
    }

    [Fact]
    public async Task ListFilteredAsync_MissedStatus_ExcludesReminderAlreadyFiredAfterDueTime()
    {
        var now = DateTime.UtcNow;
        var table = new InMemoryManagementReminderTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "missed"),
                ReminderName = "missed",
                StartAt = now.AddMinutes(-10),
                NextDueUtc = now.AddMinutes(-6),
                Period = TimeSpan.FromMinutes(1),
                LastFireUtc = null,
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "already-fired"),
                ReminderName = "already-fired",
                StartAt = now.AddMinutes(-10),
                NextDueUtc = now.AddMinutes(-6),
                Period = TimeSpan.FromMinutes(1),
                LastFireUtc = now.AddMinutes(-5),
            });
        var grain = new ReminderManagementGrain(table);

        var missed = await grain.ListFilteredAsync(
            new ReminderQueryFilter
            {
                Status = ReminderQueryStatus.Missed,
                MissedBy = TimeSpan.FromMinutes(5),
            },
            pageSize: 10);

        Assert.Equal(["missed"], missed.Reminders.Select(x => x.ReminderName).ToArray());
    }

    [Fact]
    public async Task ListOverdueAsync_UsesThreshold()
    {
        var now = DateTime.UtcNow;
        var table = new InMemoryManagementReminderTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "old"),
                ReminderName = "old",
                StartAt = now.AddMinutes(-10),
                NextDueUtc = now.AddMinutes(-8),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "recent"),
                ReminderName = "recent",
                StartAt = now.AddMinutes(-1),
                NextDueUtc = now.AddMinutes(-1),
                Period = TimeSpan.FromMinutes(1),
            });
        var grain = new ReminderManagementGrain(table);

        var page = await grain.ListOverdueAsync(TimeSpan.FromMinutes(5), pageSize: 10);

        Assert.Equal(["old"], page.Reminders.Select(x => x.ReminderName).ToArray());
    }

    [Fact]
    public async Task ListDueInRangeAsync_UsesInclusiveBounds()
    {
        var lower = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var upper = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        var table = new InMemoryManagementReminderTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "lower"),
                ReminderName = "lower",
                StartAt = lower,
                NextDueUtc = lower,
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "middle"),
                ReminderName = "middle",
                StartAt = lower.AddMinutes(30),
                NextDueUtc = lower.AddMinutes(30),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "upper"),
                ReminderName = "upper",
                StartAt = upper,
                NextDueUtc = upper,
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "outside"),
                ReminderName = "outside",
                StartAt = upper.AddSeconds(1),
                NextDueUtc = upper.AddSeconds(1),
                Period = TimeSpan.FromMinutes(1),
            });
        var grain = new ReminderManagementGrain(table);

        var page = await grain.ListDueInRangeAsync(lower, upper, pageSize: 10);

        Assert.Equal(["lower", "middle", "upper"], page.Reminders.Select(x => x.ReminderName).ToArray());
    }

    [Fact]
    public async Task MutationApis_UpdateBackingStore()
    {
        var originalDue = DateTime.UtcNow.AddMinutes(-15);
        var grainId = GrainId.Create("test", "mutations");
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            StartAt = originalDue,
            NextDueUtc = originalDue,
            Period = TimeSpan.FromMinutes(1),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-1",
        };
        var table = new InMemoryManagementReminderTable(entry);
        var grain = new ReminderManagementGrain(table);

        await grain.SetPriorityAsync(grainId, "r", ReminderPriority.High);
        await grain.SetActionAsync(grainId, "r", MissedReminderAction.Notify);
        await grain.RepairAsync(grainId, "r");

        var repaired = await table.ReadRow(grainId, "r");
        Assert.Equal(ReminderPriority.High, repaired.Priority);
        Assert.Equal(MissedReminderAction.Notify, repaired.Action);
        Assert.True(repaired.NextDueUtc > DateTime.UtcNow);

        await grain.DeleteAsync(grainId, "r");
        Assert.Null(await table.ReadRow(grainId, "r"));
    }

    [Fact]
    public async Task MutationApis_WithReminderService_RescheduleReminderChain()
    {
        var due = DateTime.UtcNow.AddMinutes(2);
        var grainId = GrainId.Create("test", "mutation-reschedule");
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "r",
            StartAt = due,
            NextDueUtc = due,
            Period = TimeSpan.FromMinutes(5),
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Skip,
            ETag = "etag-1",
        };

        var reminderTable = Substitute.For<Orleans.AdvancedReminders.IReminderTable>();
        reminderTable.ReadRow(grainId, "r").Returns(
            Task.FromResult(CloneEntry(entry)),
            Task.FromResult(CloneEntry(entry)),
            Task.FromResult(CloneEntry(entry)));
        reminderTable.UpsertRow(Arg.Any<ReminderEntry>()).Returns("etag-2", "etag-3", "etag-4");

        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = new TestManagementDispatcherGrain(GrainId.Create("sys", "dispatcher"));
        grainFactory.GetGrain<IAdvancedReminderDispatcherGrain>(grainId.ToString(), null).Returns(dispatcher);

        var jobManager = Substitute.For<ILocalDurableJobManager>();
        var service = new AdvancedReminderService(
            reminderTable,
            jobManager,
            new TestJobShardManager(),
            grainFactory,
            Options.Create(new Orleans.AdvancedReminders.ReminderOptions()),
            NullLogger<AdvancedReminderService>.Instance,
            TimeProvider.System);
        var grain = new ReminderManagementGrain(
            reminderTable,
            new ServiceCollection()
                .AddSingleton(service)
                .BuildServiceProvider());

        await grain.SetPriorityAsync(grainId, "r", ReminderPriority.High);
        await grain.SetActionAsync(grainId, "r", MissedReminderAction.Notify);
        await grain.RepairAsync(grainId, "r");

        await jobManager.Received(3).ScheduleJobAsync(
            Arg.Is<ScheduleJobRequest>(request =>
                request.JobName == "advanced-reminder:r"
                && request.Target == dispatcher.GetGrainId()
                && request.Metadata != null
                && request.Metadata["grain-id"] == grainId.ToString()
                && request.Metadata["reminder-name"] == "r"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpcomingAsync_UsesHorizon()
    {
        var now = DateTime.UtcNow;
        var table = new InMemoryManagementReminderTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "in"),
                ReminderName = "in",
                StartAt = now.AddMinutes(2),
                NextDueUtc = now.AddMinutes(2),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "out"),
                ReminderName = "out",
                StartAt = now.AddMinutes(20),
                NextDueUtc = now.AddMinutes(20),
                Period = TimeSpan.FromMinutes(1),
            });

        var grain = new ReminderManagementGrain(table);
        var reminders = (await grain.UpcomingAsync(TimeSpan.FromMinutes(5))).ToArray();

        Assert.Single(reminders);
        Assert.Equal("in", reminders[0].ReminderName);
    }

    [Fact]
    public async Task UpcomingAsync_NegativeHorizon_Throws()
    {
        var grain = new ReminderManagementGrain(new InMemoryManagementReminderTable());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.UpcomingAsync(TimeSpan.FromMinutes(-1)));

        Assert.Equal("horizon", exception.ParamName);
    }

    [Fact]
    public async Task ListFilteredAsync_WithNonPositivePageSize_Throws()
    {
        var grain = new ReminderManagementGrain(new InMemoryManagementReminderTable());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => grain.ListFilteredAsync(new ReminderQueryFilter(), pageSize: 0));

        Assert.Equal("pageSize", exception.ParamName);
    }

    [Fact]
    public async Task ListFilteredAsync_WithMalformedContinuationToken_Throws()
    {
        var grain = new ReminderManagementGrain(
            new InMemoryManagementReminderTable(
                new ReminderEntry
                {
                    GrainId = GrainId.Create("test", "invalid-token"),
                    ReminderName = "r1",
                    StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                    NextDueUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                    Period = TimeSpan.FromMinutes(1),
                }));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => grain.ListFilteredAsync(new ReminderQueryFilter(), pageSize: 1, continuationToken: "definitely-not-base64"));

        Assert.Equal("continuationToken", exception.ParamName);
    }

    [Fact]
    public async Task MutationApis_WhenReminderIsMissing_ThrowReminderException()
    {
        var grainId = GrainId.Create("test", "missing-mutation");
        var grain = new ReminderManagementGrain(new InMemoryManagementReminderTable());

        await Assert.ThrowsAsync<AdvancedReminderException>(() => grain.SetPriorityAsync(grainId, "missing", ReminderPriority.High));
        await Assert.ThrowsAsync<AdvancedReminderException>(() => grain.SetActionAsync(grainId, "missing", MissedReminderAction.Notify));
        await Assert.ThrowsAsync<AdvancedReminderException>(() => grain.RepairAsync(grainId, "missing"));
        await Assert.ThrowsAsync<AdvancedReminderException>(() => grain.DeleteAsync(grainId, "missing"));
    }

    [Fact]
    public async Task RepairAsync_ForCronReminder_RecomputesNextOccurrenceUsingStoredTimeZone()
    {
        var grainId = GrainId.Create("test", "cron-repair");
        var timeZone = AdvancedReminderTimeZoneTestHelper.GetCentralEuropeanTimeZone();
        var normalizedTimeZoneId = ReminderCronSchedule.NormalizeTimeZoneIdForStorage(timeZone) ?? timeZone.Id;
        var builder = ReminderCronBuilder.DailyAt(9, 0).InTimeZone(timeZone);
        var beforeRepair = DateTime.UtcNow;
        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "cron",
            StartAt = beforeRepair.AddDays(-2),
            NextDueUtc = beforeRepair.AddDays(-1),
            Period = TimeSpan.Zero,
            CronExpression = builder.ToExpressionString(),
            CronTimeZoneId = normalizedTimeZoneId,
            LastFireUtc = beforeRepair.AddDays(-2),
            ETag = "etag-1",
        };
        var table = new InMemoryManagementReminderTable(entry);
        var grain = new ReminderManagementGrain(table);

        await grain.RepairAsync(grainId, "cron");

        var afterRepair = DateTime.UtcNow;
        var repaired = await table.ReadRow(grainId, "cron");
        var expectedLowerBound = builder.GetNextOccurrence(beforeRepair);
        var expectedUpperBound = builder.GetNextOccurrence(afterRepair);

        Assert.NotNull(repaired.NextDueUtc);
        Assert.NotNull(expectedLowerBound);
        Assert.NotNull(expectedUpperBound);
        Assert.InRange(repaired.NextDueUtc!.Value, expectedLowerBound!.Value, expectedUpperBound!.Value);
        Assert.Equal(builder.ToExpressionString(), repaired.CronExpression);
        Assert.Equal(normalizedTimeZoneId, repaired.CronTimeZoneId);
    }

    private static ReminderEntry CloneEntry(ReminderEntry entry)
        => new()
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            ETag = entry.ETag,
            CronExpression = entry.CronExpression,
            CronTimeZoneId = entry.CronTimeZoneId,
            NextDueUtc = entry.NextDueUtc,
            LastFireUtc = entry.LastFireUtc,
            Priority = entry.Priority,
            Action = entry.Action,
        };

    private sealed class InMemoryManagementReminderTable(params ReminderEntry[] reminders) : Orleans.AdvancedReminders.IReminderTable
    {
        private readonly Dictionary<(GrainId GrainId, string ReminderName), ReminderEntry> _entries =
            reminders.ToDictionary(
                reminder => (reminder.GrainId, reminder.ReminderName),
                reminder => CloneEntry(reminder));

        public Task<ReminderTableData> ReadRows(GrainId grainId)
            => Task.FromResult(new ReminderTableData(_entries.Values.Where(entry => entry.GrainId.Equals(grainId)).Select(CloneEntry).ToList()));

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
            => Task.FromResult(new ReminderTableData(_entries.Values.Select(CloneEntry).ToList()));

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName)
        {
            _entries.TryGetValue((grainId, reminderName), out var entry);
            return Task.FromResult(entry is null ? null! : CloneEntry(entry));
        }

        public Task<string> UpsertRow(ReminderEntry entry)
        {
            var copy = CloneEntry(entry);
            copy.ETag = string.IsNullOrWhiteSpace(copy.ETag)
                ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                : copy.ETag + "-next";
            _entries[(copy.GrainId, copy.ReminderName)] = copy;
            return Task.FromResult(copy.ETag);
        }

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
            => Task.FromResult(_entries.Remove((grainId, reminderName)));

        public Task TestOnlyClearTable()
        {
            _entries.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class TestManagementDispatcherGrain : IAdvancedReminderDispatcherGrain, IGrainBase
    {
        public TestManagementDispatcherGrain(GrainId grainId)
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(grainId);
            GrainContext = context;
        }

        public IGrainContext GrainContext { get; }

        public Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestJobShardManager() : JobShardManager(SiloAddress.Zero)
    {
        public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
            => Task.FromResult(new List<IJobShard>());

        public override Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

[TestCategory("Reminders")]
[TestCategory("Stress")]
public class ReminderStressTests
{
    [Fact]
    public async Task ListFilteredAsync_HighLoad_200K_Reminders_PagesCorrectly()
    {
        const int totalReminders = 200_000;
        const int pageSize = 2_048;

        var now = DateTime.UtcNow;
        var reminders = CreateSyntheticReminders(totalReminders, now);
        var table = new SingleSegmentReminderTable(reminders);
        var grain = new ReminderManagementGrain(table);

        var filter = new ReminderQueryFilter
        {
            Priority = ReminderPriority.High,
            ScheduleKind = ReminderScheduleKind.Cron,
            Status = ReminderQueryStatus.Upcoming,
        };

        var expectedCount = reminders.Count(reminder =>
            reminder.Priority == ReminderPriority.High
            && !string.IsNullOrWhiteSpace(reminder.CronExpression)
            && (reminder.NextDueUtc ?? reminder.StartAt) > now);

        var observedCount = 0;
        var pageRequests = 0;
        string? continuationToken = null;

        do
        {
            pageRequests++;
            var page = await grain.ListFilteredAsync(filter, pageSize, continuationToken);
            Assert.InRange(page.Reminders.Count, 0, pageSize);
            observedCount += page.Reminders.Count;
            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        Assert.Equal(expectedCount, observedCount);
        Assert.InRange(table.RangeReadCallCount, 1, pageRequests);
    }

    [Fact]
    public async Task Iterator_HighLoad_StreamsOneMillionReminders()
    {
        const int totalReminders = 1_000_000;
        const int pageSize = 4_096;

        var management = new SyntheticPagedReminderManagementGrain(totalReminders);
        var iterator = new ReminderIterator(management);

        long observed = 0;
        await foreach (var _ in iterator.EnumerateAllAsync(pageSize))
        {
            observed++;
        }

        Assert.Equal(totalReminders, observed);
        Assert.True(management.ListAllCallCount > 1);
    }

    private static List<ReminderEntry> CreateSyntheticReminders(int count, DateTime nowUtc)
    {
        var result = new List<ReminderEntry>(count);
        var sharedGrainId = GrainId.Create("stress", "scan");

        for (var i = 0; i < count; i++)
        {
            var due = nowUtc.AddSeconds((i % 120) - 60);
            result.Add(new ReminderEntry
            {
                GrainId = sharedGrainId,
                ReminderName = $"r-{i.ToString(CultureInfo.InvariantCulture)}",
                StartAt = due,
                NextDueUtc = due,
                LastFireUtc = due.AddSeconds(-1),
                Period = TimeSpan.FromMinutes(1),
                CronExpression = i % 2 == 0 ? "*/5 * * * * *" : null!,
                Priority = i % 3 == 0 ? ReminderPriority.High : ReminderPriority.Normal,
                Action = (i % 3) switch
                {
                    0 => MissedReminderAction.FireImmediately,
                    1 => MissedReminderAction.Skip,
                    _ => MissedReminderAction.Notify,
                },
            });
        }

        return result;
    }

    private sealed class SingleSegmentReminderTable(List<ReminderEntry> reminders) : Orleans.AdvancedReminders.IReminderTable
    {
        public int RangeReadCallCount { get; private set; }

        public Task<ReminderTableData> ReadRows(GrainId grainId) => throw new NotSupportedException();

        public Task<ReminderTableData> ReadRows(uint begin, uint end)
        {
            RangeReadCallCount++;
            return Task.FromResult(new ReminderTableData(reminders));
        }

        public Task<ReminderEntry> ReadRow(GrainId grainId, string reminderName) => throw new NotSupportedException();

        public Task<string> UpsertRow(ReminderEntry entry) => throw new NotSupportedException();

        public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => throw new NotSupportedException();

        public Task TestOnlyClearTable() => throw new NotSupportedException();
    }

    private sealed class SyntheticPagedReminderManagementGrain(int totalReminders) : IReminderManagementGrain
    {
        private const string TokenPrefix = "offset:";
        private readonly GrainId _sharedGrainId = GrainId.Create("stress", "iterator");

        public int ListAllCallCount { get; private set; }

        public Task<ReminderManagementPage> ListAllAsync(int pageSize = 256, string? continuationToken = null)
        {
            ListAllCallCount++;
            var offset = ParseOffset(continuationToken);
            if (offset >= totalReminders)
            {
                return Task.FromResult(new ReminderManagementPage { Reminders = [], ContinuationToken = null });
            }

            var take = Math.Min(pageSize, totalReminders - offset);
            var reminders = new List<ReminderEntry>(take);
            for (var i = 0; i < take; i++)
            {
                reminders.Add(new ReminderEntry
                {
                    GrainId = _sharedGrainId,
                    ReminderName = $"bulk-{offset + i:0000000}",
                    StartAt = DateTime.UnixEpoch,
                    NextDueUtc = DateTime.UnixEpoch,
                    Period = TimeSpan.FromMinutes(1),
                    Priority = ReminderPriority.Normal,
                    Action = MissedReminderAction.Skip,
                });
            }

            var nextOffset = offset + take;
            return Task.FromResult(new ReminderManagementPage
            {
                Reminders = reminders,
                ContinuationToken = nextOffset < totalReminders ? TokenPrefix + nextOffset.ToString(CultureInfo.InvariantCulture) : null,
            });
        }

        public Task<ReminderManagementPage> ListOverdueAsync(TimeSpan overdueBy, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();
        public Task<ReminderManagementPage> ListDueInRangeAsync(DateTime fromUtcInclusive, DateTime toUtcInclusive, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();
        public Task<ReminderManagementPage> ListFilteredAsync(ReminderQueryFilter filter, int pageSize = 256, string? continuationToken = null) => throw new NotSupportedException();
        public Task<IEnumerable<ReminderEntry>> UpcomingAsync(TimeSpan horizon) => throw new NotSupportedException();
        public Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId) => throw new NotSupportedException();
        public Task<int> CountAllAsync() => throw new NotSupportedException();
        public Task SetPriorityAsync(GrainId grainId, string name, ReminderPriority priority) => throw new NotSupportedException();
        public Task SetActionAsync(GrainId grainId, string name, MissedReminderAction action) => throw new NotSupportedException();
        public Task RepairAsync(GrainId grainId, string name) => throw new NotSupportedException();
        public Task DeleteAsync(GrainId grainId, string name) => throw new NotSupportedException();

        private static int ParseOffset(string? continuationToken)
        {
            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                return 0;
            }

            if (!continuationToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("Invalid continuation token format.", nameof(continuationToken));
            }

            var payload = continuationToken.AsSpan(TokenPrefix.Length);
            if (!int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            {
                throw new ArgumentException("Invalid continuation token payload.", nameof(continuationToken));
            }

            return offset;
        }
    }
}
