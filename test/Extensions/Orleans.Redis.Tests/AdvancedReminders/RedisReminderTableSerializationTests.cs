#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.AdvancedReminders.Redis;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using ReminderPriority = Orleans.AdvancedReminders.Runtime.ReminderPriority;
using MissedReminderAction = Orleans.AdvancedReminders.Runtime.MissedReminderAction;
using AdvancedRedisReminderTableOptions = Orleans.AdvancedReminders.Redis.RedisReminderTableOptions;

namespace Tester.Redis.AdvancedReminders;

[TestCategory("Redis"), TestCategory("Reminders")]
public class RedisReminderTableSerializationTests
{
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
            Priority = ReminderPriority.Normal,
            Action = MissedReminderAction.Notify,
        };

        var (_, payload) = InvokeConvertFromEntry(entry);
        var segments = ParseSegments(payload);

        Assert.Equal(JTokenType.Integer, segments[9]!.Type);
        Assert.Equal((int)ReminderPriority.Normal, segments[9]!.Value<int>());
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
    public void ConvertToEntry_ParsesNumericPriorityAndAction()
    {
        var grainId = GrainId.Create("test", "redis-parse-numeric");
        var payload = BuildPayload(grainId, ReminderPriority.High, MissedReminderAction.FireImmediately, numericEnums: true);

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(grainId, entry.GrainId);
        Assert.Equal(ReminderPriority.High, entry.Priority);
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

        var exception = Assert.Throws<TargetInvocationException>(() => InvokeConvertToEntry(payload));
        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public void ConvertToEntry_DefaultsPriorityAndActionWhenValuesAreInvalid()
    {
        var grainId = GrainId.Create("test", "redis-default-invalid");
        var payload = BuildPayloadWithCustomEnums(grainId, priorityToken: "999", actionToken: "-3");

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(ReminderPriority.Normal, entry.Priority);
        Assert.Equal(MissedReminderAction.Skip, entry.Action);
    }

    private static string BuildPayload(GrainId grainId, ReminderPriority priority, MissedReminderAction action, bool numericEnums)
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
        var payload = BuildPayload(grainId, ReminderPriority.Normal, MissedReminderAction.Skip, numericEnums: true);
        var segments = ParseSegments(payload);
        segments.Add(timeZoneId);
        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static string BuildPayloadWithInsertedTimeZone(GrainId grainId, string timeZoneId)
    {
        var payload = BuildPayload(grainId, ReminderPriority.Normal, MissedReminderAction.Skip, numericEnums: true);
        var segments = ParseSegments(payload);
        segments.Insert(7, timeZoneId);
        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static ReminderEntry InvokeConvertToEntry(string payload)
    {
        var method = typeof(RedisReminderTable).GetMethod("ConvertToEntry", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [payload]);
        Assert.NotNull(result);
        return (ReminderEntry)result!;
    }

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
