#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Orleans.AdvancedReminders.Redis;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;
using Xunit;
using AdvancedRedisReminderTableOptions = Orleans.AdvancedReminders.Redis.RedisReminderTableOptions;
using MissedReminderAction = Orleans.AdvancedReminders.Runtime.MissedReminderAction;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using DurableJobPriority = Orleans.DurableJobs.DurableJobPriority;

namespace Tester.Redis.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
[TestCategory("Redis"), TestCategory("Reminders")]
public class RedisReminderTableSerializationTests
{
    [Fact]
    public async Task StartAsync_WhenConnectionCreationOutlivesCancellation_DisposesLateOwnedMultiplexer()
    {
        var creation = new TaskCompletionSource<(IConnectionMultiplexer Multiplexer, bool IsShared)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.DisposeAsync().Returns(_ =>
        {
            disposed.TrySetResult();
            return ValueTask.CompletedTask;
        });
        var options = Options.Create(new AdvancedRedisReminderTableOptions
        {
            CreateMultiplexer = _ => creation.Task,
        });
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = "test-service",
            ClusterId = "test-cluster",
        });
        var table = new RedisReminderTable(
            NullLogger<RedisReminderTable>.Instance,
            clusterOptions,
            options);
        using var cancellation = new CancellationTokenSource();

        var startTask = table.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);

        creation.SetResult((multiplexer, IsShared: false));
        await disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await multiplexer.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenInitializationFailsAfterCreatingConnection_DisposesOwnedMultiplexer()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase().Returns(_ => throw new InvalidOperationException("database unavailable"));
        var options = Options.Create(new AdvancedRedisReminderTableOptions
        {
            CreateMultiplexer = _ => Task.FromResult((multiplexer, IsShared: false)),
        });
        var clusterOptions = Options.Create(new ClusterOptions
        {
            ServiceId = "test-service",
            ClusterId = "test-cluster",
        });
        var table = new RedisReminderTable(
            NullLogger<RedisReminderTable>.Instance,
            clusterOptions,
            options);

        var exception = await Assert.ThrowsAsync<RedisRemindersException>(
            () => table.StartAsync(CancellationToken.None));

        Assert.Contains("database unavailable", exception.Message, StringComparison.Ordinal);
        await multiplexer.Received(1).DisposeAsync();
    }

    [Fact]
    public void ConvertFromEntry_WritesPriorityAndActionAsNumbers()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "redis-serialization"),
            ReminderName = "r",
            StartAt = DateTime.UtcNow,
            Period = TimeSpan.FromSeconds(30),
            CronExpression = "*/5 * * * * *",
            NextDueUtc = DateTime.UtcNow.AddSeconds(5),
            LastFireUtc = DateTime.UtcNow,
            Priority = DurableJobPriority.Normal,
            Action = MissedReminderAction.Notify,
        };

        var (_, payload) = InvokeConvertFromEntry(entry);
        var segments = ParseSegments(payload);

        Assert.Equal(JTokenType.Integer, segments[9]!.Type);
        Assert.Equal((int)DurableJobPriority.Normal, segments[9]!.Value<int>());
        Assert.Equal(JTokenType.Integer, segments[10]!.Type);
        Assert.Equal((int)MissedReminderAction.Notify, segments[10]!.Value<int>());
    }

    [Fact]
    public void ConvertFromEntry_WritesInvariantTemporalFormats()
    {
        var startAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var period = TimeSpan.FromMinutes(5);
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "redis-temporal-format"),
            ReminderName = "r",
            StartAt = startAt,
            Period = period,
            NextDueUtc = startAt.AddMinutes(5),
            LastFireUtc = startAt.AddMinutes(-1),
        };

        var (_, payload) = InvokeConvertFromEntry(entry);
        var segments = ParseSegments(payload);

        Assert.Equal(startAt.ToString("O", CultureInfo.InvariantCulture), segments[4]!.Value<string>());
        Assert.Equal(period.ToString("c", CultureInfo.InvariantCulture), segments[5]!.Value<string>());
        Assert.Equal(entry.NextDueUtc?.ToString("O", CultureInfo.InvariantCulture), segments[7]!.Value<string>());
        Assert.Equal(entry.LastFireUtc?.ToString("O", CultureInfo.InvariantCulture), segments[8]!.Value<string>());
    }

    [Fact]
    public void ConvertFromEntry_WritesCronTimeZoneAtTailSegment()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "redis-timezone-tail"),
            ReminderName = "r",
            StartAt = DateTime.UtcNow,
            Period = TimeSpan.FromMinutes(1),
            CronExpression = "0 9 * * *",
            CronTimeZoneId = "America/New_York",
        };

        var (_, payload) = InvokeConvertFromEntry(entry);
        var segments = ParseSegments(payload);

        Assert.Equal(entry.CronTimeZoneId, segments[11]!.Value<string>());
    }

    [Fact]
    public void ConvertFromEntry_RoundTripsDurableJobIdentityFields()
    {
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "redis-job-fields"),
            ReminderName = "r",
            StartAt = DateTime.UtcNow,
            Period = TimeSpan.FromMinutes(1),
            ScheduleId = "schedule-1",
            JobId = "job-1",
            JobShardId = "shard-1",
        };

        var (_, payload) = InvokeConvertFromEntry(entry);
        var segments = ParseSegments(payload);
        var roundTripped = InvokeConvertToEntry(payload);

        Assert.Equal(entry.ScheduleId, segments[12]!.Value<string>());
        Assert.Equal(entry.JobId, segments[13]!.Value<string>());
        Assert.Equal(entry.JobShardId, segments[14]!.Value<string>());
        Assert.Equal(entry.ScheduleId, roundTripped.ScheduleId);
        Assert.Equal(entry.JobId, roundTripped.JobId);
        Assert.Equal(entry.JobShardId, roundTripped.JobShardId);
    }

    [Fact]
    public void ConvertToEntry_LegacyPayloadDefaultsDurableJobIdentityFields()
    {
        var payload = BuildPayload(
            GrainId.Create("test", "redis-legacy"),
            DurableJobPriority.Normal,
            MissedReminderAction.Skip,
            numericEnums: true);

        var entry = InvokeConvertToEntry(payload);

        Assert.Empty(entry.ScheduleId);
        Assert.Empty(entry.JobId);
        Assert.Empty(entry.JobShardId);
    }

    [Fact]
    public void ConvertToEntry_ReusesTemporaryUtf8PayloadBuffer()
    {
        const int IterationCount = 10_000;
        var payload = (RedisValue)BuildPayload(
            GrainId.Create("redis-allocation", "grain"),
            DurableJobPriority.High,
            MissedReminderAction.Notify,
            numericEnums: true);

        for (var index = 0; index < 10; index++)
        {
            _ = RedisReminderTable.ConvertToEntry(payload);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < IterationCount; index++)
        {
            _ = RedisReminderTable.ConvertToEntry(payload);
        }

        var allocatedBytesPerEntry = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / IterationCount;
        // The ReminderEntry and its decoded identity fields necessarily allocate. Keep the
        // temporary JSON envelope and temporal strings out of that budget: parsing via a
        // JsonDocument plus DateTime/TimeSpan strings used roughly 776 bytes per entry here.
        Assert.InRange(allocatedBytesPerEntry, 0, 512);
    }

    [Fact]
    public void ConvertToEntry_ParsesNumericPriorityAndAction()
    {
        var grainId = GrainId.Create("test", "redis-parse-numeric");
        var payload = BuildPayload(grainId, DurableJobPriority.Low, MissedReminderAction.FireImmediately, numericEnums: true);

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(grainId, entry.GrainId);
        Assert.Equal(DurableJobPriority.Low, entry.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, entry.Action);
    }

    [Fact]
    public void ConvertToEntry_ParsesCronTimeZoneFromCurrentLayout()
    {
        var grainId = GrainId.Create("test", "redis-parse-timezone-current");
        var payload = BuildPayloadWithAppendedTimeZone(grainId, "Europe/Kyiv");

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal("Europe/Kyiv", entry.CronTimeZoneId);
    }

    [Fact]
    public void ConvertToEntry_RejectsNonCanonicalTimeZoneSegmentOrder()
    {
        var grainId = GrainId.Create("test", "redis-parse-timezone-wrong-order");
        var payload = BuildPayloadWithInsertedTimeZone(grainId, "Europe/Kyiv");

        Assert.Throws<FormatException>(() => InvokeConvertToEntry(payload));
    }

    [Fact]
    public void ConvertToEntry_DefaultsPriorityAndActionWhenValuesAreInvalid()
    {
        var grainId = GrainId.Create("test", "redis-default-invalid");
        var payload = BuildPayloadWithCustomEnums(grainId, priorityToken: "999", actionToken: "-3");

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(DurableJobPriority.Normal, entry.Priority);
        Assert.Equal(MissedReminderAction.Skip, entry.Action);
    }

    private static string BuildPayload(GrainId grainId, DurableJobPriority priority, MissedReminderAction action, bool numericEnums)
    {
        var startAt = DateTime.UtcNow;
        var nextDueUtc = startAt.AddSeconds(1);
        var lastFireUtc = startAt;
        var grainHash = grainId.GetUniformHashCode().ToString("X8", CultureInfo.InvariantCulture);
        object priorityToken = numericEnums ? (int)priority : ((int)priority).ToString(CultureInfo.InvariantCulture);
        object actionToken = numericEnums ? (int)action : ((int)action).ToString(CultureInfo.InvariantCulture);

        var segments = new object[]
        {
            grainHash,
            grainId.ToString(),
            "reminder",
            "etag",
            startAt.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(10).ToString("c", CultureInfo.InvariantCulture),
            "*/5 * * * * *",
            nextDueUtc.ToString("O", CultureInfo.InvariantCulture),
            lastFireUtc.ToString("O", CultureInfo.InvariantCulture),
            priorityToken,
            actionToken,
        };

        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static string BuildPayloadWithCustomEnums(GrainId grainId, object priorityToken, object actionToken)
    {
        var startAt = DateTime.UtcNow;
        var nextDueUtc = startAt.AddSeconds(1);
        var lastFireUtc = startAt;
        var grainHash = grainId.GetUniformHashCode().ToString("X8", CultureInfo.InvariantCulture);
        var segments = new object[]
        {
            grainHash,
            grainId.ToString(),
            "reminder",
            "etag",
            startAt.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(10).ToString("c", CultureInfo.InvariantCulture),
            "*/5 * * * * *",
            nextDueUtc.ToString("O", CultureInfo.InvariantCulture),
            lastFireUtc.ToString("O", CultureInfo.InvariantCulture),
            priorityToken,
            actionToken,
        };

        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static string BuildPayloadWithAppendedTimeZone(GrainId grainId, string timeZoneId)
    {
        var payload = BuildPayload(grainId, DurableJobPriority.Normal, MissedReminderAction.Skip, numericEnums: true);
        var segments = ParseSegments(payload);
        segments.Add(timeZoneId);
        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static string BuildPayloadWithInsertedTimeZone(GrainId grainId, string timeZoneId)
    {
        var payload = BuildPayload(grainId, DurableJobPriority.Normal, MissedReminderAction.Skip, numericEnums: true);
        var segments = ParseSegments(payload);
        segments.Insert(7, timeZoneId);
        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static ReminderEntry InvokeConvertToEntry(string payload)
        => RedisReminderTable.ConvertToEntry((RedisValue)payload);

    private static (string ETag, string Payload) InvokeConvertFromEntry(ReminderEntry entry)
    {
        var table = new RedisReminderTable(
            NullLogger<RedisReminderTable>.Instance,
            Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" }),
            Options.Create(new AdvancedRedisReminderTableOptions()));

        var method = typeof(RedisReminderTable).GetMethod("ConvertFromEntry", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(table, [entry]);
        Assert.NotNull(result);

        var pair = ((RedisValue, RedisValue))result!;
        return ((string)pair.Item1!, (string)pair.Item2!);
    }

    private static JArray ParseSegments(string payload)
    {
        using var stringReader = new StringReader($"[{payload}]");
        using var jsonReader = new JsonTextReader(stringReader)
        {
            DateParseHandling = DateParseHandling.None,
        };

        return JArray.Load(jsonReader);
    }
}
