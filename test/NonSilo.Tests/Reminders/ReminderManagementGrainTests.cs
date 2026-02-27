#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests.Reminders;

[TestCategory("Reminders")]
public class ReminderManagementGrainTests
{
    [Fact]
    public async Task UpcomingAsync_OrdersByPriorityThenDueAndAppliesHorizon()
    {
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = CreateScanAwareTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "n1"),
                ReminderName = "normal",
                Priority = ReminderPriority.Normal,
                NextDueUtc = now.UtcDateTime.AddMinutes(2),
                StartAt = now.UtcDateTime.AddMinutes(2),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "c2"),
                ReminderName = "critical-later",
                Priority = ReminderPriority.High,
                NextDueUtc = now.UtcDateTime.AddMinutes(3),
                StartAt = now.UtcDateTime.AddMinutes(3),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "c1"),
                ReminderName = "critical-first",
                Priority = ReminderPriority.High,
                NextDueUtc = now.UtcDateTime.AddMinutes(1),
                StartAt = now.UtcDateTime.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "bg"),
                ReminderName = "out-of-range",
                Priority = ReminderPriority.Normal,
                NextDueUtc = now.UtcDateTime.AddMinutes(20),
                StartAt = now.UtcDateTime.AddMinutes(20),
                Period = TimeSpan.FromMinutes(1),
            }
        );

        var grain = new ReminderManagementGrain(table, timeProvider);
        var reminders = (await grain.UpcomingAsync(TimeSpan.FromMinutes(5))).ToArray();

        Assert.Equal(["critical-first", "critical-later", "normal"], reminders.Select(r => r.ReminderName).ToArray());
    }

    [Fact]
    public async Task UpcomingAsync_NegativeHorizon_Throws()
    {
        var grain = new ReminderManagementGrain(Substitute.For<IReminderTable>(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.UpcomingAsync(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task ListAllAsync_ReturnsSortedPagesWithContinuationToken()
    {
        var table = CreateScanAwareTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g2"),
                ReminderName = "r2",
                StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g1"),
                ReminderName = "rB",
                StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g1"),
                ReminderName = "rA",
                StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(1),
            }
        );

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());

        var first = await grain.ListAllAsync(pageSize: 2);
        Assert.Equal(2, first.Reminders.Count);
        Assert.Equal(["rA", "rB"], first.Reminders.Select(reminder => reminder.ReminderName).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(first.ContinuationToken));

        var second = await grain.ListAllAsync(pageSize: 2, continuationToken: first.ContinuationToken);
        Assert.Single(second.Reminders);
        Assert.Equal("r2", second.Reminders[0].ReminderName);
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task ListAllAsync_InvalidPagingInputs_Throw()
    {
        var grain = new ReminderManagementGrain(Substitute.For<IReminderTable>(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListAllAsync(pageSize: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListAllAsync(pageSize: 5_000));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.ListAllAsync(pageSize: 10, continuationToken: "not-a-token"));
    }

    [Fact]
    public async Task ListAllAsync_InvalidContinuationTokenPayloads_Throw()
    {
        var grain = new ReminderManagementGrain(Substitute.For<IReminderTable>(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentException>(() => grain.ListAllAsync(pageSize: 10, continuationToken: EncodeToken("missing-separator")));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.ListAllAsync(pageSize: 10, continuationToken: EncodeToken("x:grain")));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.ListAllAsync(pageSize: 10, continuationToken: EncodeToken("5:ab")));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.ListAllAsync(pageSize: 10, continuationToken: EncodeToken("0:reminder")));
    }

    [Fact]
    public async Task ListAllAsync_BoundedSelection_ReturnsLexicographicallySmallestPage()
    {
        var grainId = GrainId.Create("test", "single-grain");
        var reminders = Enumerable.Range(0, 10)
            .Select(i => new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = $"r{9 - i}",
                StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(1),
            })
            .ToArray();

        var table = Substitute.For<IReminderTable>();
        var yieldedData = false;
        table.ReadRows(Arg.Any<uint>(), Arg.Any<uint>())
            .Returns(_ =>
            {
                if (yieldedData)
                {
                    return Task.FromResult(new ReminderTableData([]));
                }

                yieldedData = true;
                return Task.FromResult(new ReminderTableData(reminders));
            });

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());

        var page = await grain.ListAllAsync(pageSize: 2);

        Assert.Equal(["r0", "r1"], page.Reminders.Select(reminder => reminder.ReminderName).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(page.ContinuationToken));
    }

    [Fact]
    public async Task ListOverdueAsync_FiltersByThreshold_AndUsesStartAtFallback()
    {
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = CreateScanAwareTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "overdue-next"),
                ReminderName = "overdue-next",
                NextDueUtc = now.UtcDateTime.AddMinutes(-5),
                StartAt = now.UtcDateTime.AddMinutes(-5),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "overdue-start"),
                ReminderName = "overdue-start",
                NextDueUtc = null,
                StartAt = now.UtcDateTime.AddMinutes(-3),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "fresh"),
                ReminderName = "fresh",
                NextDueUtc = now.UtcDateTime.AddMinutes(-1),
                StartAt = now.UtcDateTime.AddMinutes(-1),
                Period = TimeSpan.FromMinutes(1),
            }
        );

        var grain = new ReminderManagementGrain(table, timeProvider);
        var page = await grain.ListOverdueAsync(overdueBy: TimeSpan.FromMinutes(2), pageSize: 10);

        Assert.Equal(2, page.Reminders.Count);
        Assert.Equal(["overdue-next", "overdue-start"], page.Reminders.Select(reminder => reminder.ReminderName).ToArray());
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task ListOverdueAsync_NegativeOverdueBy_Throws()
    {
        var grain = new ReminderManagementGrain(Substitute.For<IReminderTable>(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => grain.ListOverdueAsync(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public async Task ListDueInRangeAsync_FiltersByDueTimestamp()
    {
        var from = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var to = from.AddMinutes(10);

        var table = CreateScanAwareTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "in-range-1"),
                ReminderName = "in-range-1",
                NextDueUtc = from.AddMinutes(1),
                StartAt = from.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "in-range-2"),
                ReminderName = "in-range-2",
                NextDueUtc = null,
                StartAt = from.AddMinutes(5),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "out-of-range"),
                ReminderName = "out-of-range",
                NextDueUtc = to.AddMinutes(1),
                StartAt = to.AddMinutes(1),
                Period = TimeSpan.FromMinutes(1),
            });

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());
        var page = await grain.ListDueInRangeAsync(from, to, pageSize: 10);

        Assert.Equal(["in-range-1", "in-range-2"], page.Reminders.Select(x => x.ReminderName).OrderBy(static x => x).ToArray());
    }

    [Fact]
    public async Task ListDueInRangeAsync_InvalidUtcInputs_Throw()
    {
        var grain = new ReminderManagementGrain(Substitute.For<IReminderTable>(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.ListDueInRangeAsync(
                new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local),
                new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.ListDueInRangeAsync(
                new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Local)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            grain.ListDueInRangeAsync(
                new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task ListFilteredAsync_AppliesStatusesAndAdditionalFilters()
    {
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var table = CreateScanAwareTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "match"),
                ReminderName = "match",
                StartAt = now.UtcDateTime.AddMinutes(-20),
                Period = TimeSpan.Zero,
                CronExpression = "*/5 * * * * *",
                NextDueUtc = now.UtcDateTime.AddMinutes(-10),
                LastFireUtc = now.UtcDateTime.AddMinutes(-20),
                Priority = ReminderPriority.High,
                Action = MissedReminderAction.FireImmediately,
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "overdue-not-missed"),
                ReminderName = "overdue-not-missed",
                StartAt = now.UtcDateTime.AddMinutes(-10),
                Period = TimeSpan.FromMinutes(1),
                CronExpression = null,
                NextDueUtc = now.UtcDateTime.AddMinutes(-5),
                LastFireUtc = now.UtcDateTime.AddMinutes(-1),
                Priority = ReminderPriority.High,
                Action = MissedReminderAction.FireImmediately,
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "wrong-priority"),
                ReminderName = "wrong-priority",
                StartAt = now.UtcDateTime.AddMinutes(-20),
                Period = TimeSpan.Zero,
                CronExpression = "*/5 * * * * *",
                NextDueUtc = now.UtcDateTime.AddMinutes(-10),
                LastFireUtc = null,
                Priority = ReminderPriority.Normal,
                Action = MissedReminderAction.FireImmediately,
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "upcoming"),
                ReminderName = "upcoming",
                StartAt = now.UtcDateTime.AddMinutes(2),
                Period = TimeSpan.FromMinutes(1),
                CronExpression = null,
                NextDueUtc = now.UtcDateTime.AddMinutes(2),
                LastFireUtc = null,
                Priority = ReminderPriority.High,
                Action = MissedReminderAction.FireImmediately,
            });

        var filter = new ReminderQueryFilter
        {
            DueFromUtcInclusive = now.UtcDateTime.AddMinutes(-15),
            DueToUtcInclusive = now.UtcDateTime.AddMinutes(-1),
            Priority = ReminderPriority.High,
            Action = MissedReminderAction.FireImmediately,
            ScheduleKind = ReminderScheduleKind.Cron,
            Status = ReminderQueryStatus.Overdue | ReminderQueryStatus.Missed,
            OverdueBy = TimeSpan.FromMinutes(2),
            MissedBy = TimeSpan.FromMinutes(2),
        };

        var grain = new ReminderManagementGrain(table, timeProvider);
        var page = await grain.ListFilteredAsync(filter, pageSize: 10);

        Assert.Single(page.Reminders);
        Assert.Equal("match", page.Reminders[0].ReminderName);
    }

    [Fact]
    public async Task ListFilteredAsync_InvalidFilterInputs_Throw()
    {
        var grain = new ReminderManagementGrain(Substitute.For<IReminderTable>(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentNullException>(() => grain.ListFilteredAsync(null!, pageSize: 10));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            DueFromUtcInclusive = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local),
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            DueToUtcInclusive = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local),
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            DueFromUtcInclusive = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            DueToUtcInclusive = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            OverdueBy = TimeSpan.FromMilliseconds(-1),
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            MissedBy = TimeSpan.FromMilliseconds(-1),
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            Priority = (ReminderPriority)255,
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            Action = (MissedReminderAction)255,
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            ScheduleKind = (ReminderScheduleKind)255,
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.ListFilteredAsync(new ReminderQueryFilter
        {
            Status = (ReminderQueryStatus)0x80,
        }));
    }

    [Fact]
    public async Task CountAllAsync_SumsAcrossScanRanges()
    {
        var table = CreateScanAwareTable(
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g1"),
                ReminderName = "r1",
                StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(1),
            },
            new ReminderEntry
            {
                GrainId = GrainId.Create("test", "g2"),
                ReminderName = "r2",
                StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(1),
            });

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());

        var count = await grain.CountAllAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SetPriorityAsync_InvalidPriority_Throws()
    {
        var grain = new ReminderManagementGrain(Substitute.For<IReminderTable>(), new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => grain.SetPriorityAsync(GrainId.Create("test", "g"), "r", (ReminderPriority)255));
    }

    [Fact]
    public async Task SetPriorityAsync_MissingEntry_DoesNotUpsert()
    {
        var table = Substitute.For<IReminderTable>();
        table.ReadRow(Arg.Any<GrainId>(), Arg.Any<string>()).Returns(Task.FromResult<ReminderEntry>(null!));
        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());

        await grain.SetPriorityAsync(GrainId.Create("test", "g"), "r", ReminderPriority.High);

        Assert.DoesNotContain(table.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IReminderTable.UpsertRow));
    }

    [Fact]
    public async Task SetPriorityAsync_UpdatesAndPersistsEntry()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            ETag = "etag-1",
            Priority = ReminderPriority.Normal,
        };

        ReminderEntry? upserted = null;
        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.UpsertRow(Arg.Do<ReminderEntry>(value => upserted = value)).Returns(Task.FromResult("etag-2"));

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());
        await grain.SetPriorityAsync(entry.GrainId, entry.ReminderName, ReminderPriority.High);

        Assert.NotNull(upserted);
        Assert.Equal(ReminderPriority.High, upserted!.Priority);
        Assert.Equal("etag-2", entry.ETag);
    }

    [Fact]
    public async Task SetPriorityAsync_ConflictingUpdate_Throws()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            ETag = "etag-1",
            Priority = ReminderPriority.Normal,
        };

        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.UpsertRow(entry).Returns(Task.FromResult<string>(null!));

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());
        await Assert.ThrowsAsync<ReminderException>(
            () => grain.SetPriorityAsync(entry.GrainId, entry.ReminderName, ReminderPriority.High));
    }

    [Fact]
    public async Task SetActionAsync_UpdatesAndPersistsEntry()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            ETag = "etag-1",
            Action = MissedReminderAction.Skip,
        };

        ReminderEntry? upserted = null;
        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.UpsertRow(Arg.Do<ReminderEntry>(value => upserted = value)).Returns(Task.FromResult("etag-2"));

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());
        await grain.SetActionAsync(entry.GrainId, entry.ReminderName, MissedReminderAction.Notify);

        Assert.NotNull(upserted);
        Assert.Equal(MissedReminderAction.Notify, upserted!.Action);
        Assert.Equal("etag-2", entry.ETag);
    }

    [Fact]
    public async Task SetActionAsync_ConflictingUpdate_Throws()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            ETag = "etag-1",
            Action = MissedReminderAction.Skip,
        };

        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.UpsertRow(entry).Returns(Task.FromResult<string>(null!));

        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());
        await Assert.ThrowsAsync<ReminderException>(
            () => grain.SetActionAsync(entry.GrainId, entry.ReminderName, MissedReminderAction.Notify));
    }

    [Fact]
    public async Task RepairAsync_Cron_RecomputesNextDueInFuture()
    {
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 5, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            CronExpression = "*/10 * * * * *",
            NextDueUtc = now.UtcDateTime.AddMinutes(-1),
            StartAt = now.UtcDateTime.AddMinutes(-1),
            ETag = "etag-1",
        };

        ReminderEntry? upserted = null;
        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.UpsertRow(Arg.Do<ReminderEntry>(value => upserted = value)).Returns(Task.FromResult("etag-2"));

        var grain = new ReminderManagementGrain(table, timeProvider);
        await grain.RepairAsync(entry.GrainId, entry.ReminderName);

        Assert.NotNull(upserted);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 10, DateTimeKind.Utc), upserted!.NextDueUtc);
        Assert.Equal("etag-2", entry.ETag);
    }

    [Fact]
    public async Task RepairAsync_Interval_SkipsToNextFutureTick()
    {
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            Period = TimeSpan.FromSeconds(30),
            NextDueUtc = new DateTime(2026, 1, 1, 9, 58, 45, DateTimeKind.Utc),
            StartAt = new DateTime(2026, 1, 1, 9, 58, 45, DateTimeKind.Utc),
            ETag = "etag-1",
        };

        ReminderEntry? upserted = null;
        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.UpsertRow(Arg.Do<ReminderEntry>(value => upserted = value)).Returns(Task.FromResult("etag-2"));

        var grain = new ReminderManagementGrain(table, timeProvider);
        await grain.RepairAsync(entry.GrainId, entry.ReminderName);

        Assert.NotNull(upserted);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 15, DateTimeKind.Utc), upserted!.NextDueUtc);
    }

    [Fact]
    public async Task RepairAsync_ConflictingUpdate_Throws()
    {
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            Period = TimeSpan.FromSeconds(30),
            NextDueUtc = now.UtcDateTime.AddSeconds(-30),
            StartAt = now.UtcDateTime.AddSeconds(-30),
            ETag = "etag-1",
        };

        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.UpsertRow(entry).Returns(Task.FromResult<string>(null!));
        var grain = new ReminderManagementGrain(table, timeProvider);

        await Assert.ThrowsAsync<ReminderException>(() => grain.RepairAsync(entry.GrainId, entry.ReminderName));
    }

    [Fact]
    public async Task DeleteAsync_ConflictingDelete_Throws()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            ETag = "etag-1",
        };

        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        table.RemoveRow(entry.GrainId, entry.ReminderName, entry.ETag).Returns(Task.FromResult(false));
        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());

        await Assert.ThrowsAsync<ReminderException>(() => grain.DeleteAsync(entry.GrainId, entry.ReminderName));
    }

    [Fact]
    public async Task RepairAsync_WithoutAnySchedule_DoesNotPersist()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "g"),
            ReminderName = "r",
            Period = TimeSpan.Zero,
            CronExpression = null,
            NextDueUtc = null,
            StartAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            ETag = "etag-1",
        };

        var table = Substitute.For<IReminderTable>();
        table.ReadRow(entry.GrainId, entry.ReminderName).Returns(Task.FromResult(entry));
        var grain = new ReminderManagementGrain(table, new FakeTimeProvider());

        await grain.RepairAsync(entry.GrainId, entry.ReminderName);

        Assert.DoesNotContain(table.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(IReminderTable.UpsertRow));
    }

    private static IReminderTable CreateScanAwareTable(params ReminderEntry[] reminders)
    {
        var table = Substitute.For<IReminderTable>();
        table.ReadRows(Arg.Any<uint>(), Arg.Any<uint>())
            .Returns(callInfo =>
            {
                var begin = callInfo.ArgAt<uint>(0);
                var end = callInfo.ArgAt<uint>(1);

                var filtered = reminders.Where(reminder =>
                {
                    var hash = reminder.GrainId.GetUniformHashCode();
                    return begin < end
                        ? hash > begin && hash <= end
                        : hash > begin || hash <= end;
                });

                return Task.FromResult(new ReminderTableData(filtered));
            });

        return table;
    }

    private static string EncodeToken(string raw)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
}
