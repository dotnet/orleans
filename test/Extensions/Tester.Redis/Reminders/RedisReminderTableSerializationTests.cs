using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans.Configuration;
using Orleans.Reminders.Redis;
using Orleans.Runtime;
using StackExchange.Redis;
using Xunit;

namespace Tester.Redis.Reminders;

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
    public void ConvertToEntry_PreservesUtcTimestampsWithoutTimezoneShift()
    {
        var grainId = GrainId.Create("test", "redis-utc-roundtrip");
        const string startAtText = "2026-02-07T00:26:41.7666380Z";
        const string nextDueUtcText = "2026-02-07T00:27:41.7666380Z";
        const string lastFireUtcText = "2026-02-07T00:25:41.7666380Z";
        var payload = BuildPayloadWithCustomTimestamps(grainId, startAtText, nextDueUtcText, lastFireUtcText);

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(DateTime.Parse(startAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), entry.StartAt);
        Assert.Equal(DateTime.Parse(nextDueUtcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), entry.NextDueUtc);
        Assert.Equal(DateTime.Parse(lastFireUtcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), entry.LastFireUtc);
    }

    [Fact]
    public void ConvertToEntry_ParsesNumericPriorityAndAction()
    {
        var grainId = GrainId.Create("test", "redis-parse-numeric");
        var payload = BuildPayload(
            grainId,
            ReminderPriority.High,
            MissedReminderAction.FireImmediately,
            numericEnums: true);

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(grainId, entry.GrainId);
        Assert.Equal(ReminderPriority.High, entry.Priority);
        Assert.Equal(MissedReminderAction.FireImmediately, entry.Action);
    }

    [Fact]
    public void ConvertToEntry_ParsesLegacyStringPriorityAndAction()
    {
        var grainId = GrainId.Create("test", "redis-parse-string");
        var payload = BuildPayload(
            grainId,
            ReminderPriority.Normal,
            MissedReminderAction.Skip,
            numericEnums: false);

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(grainId, entry.GrainId);
        Assert.Equal(ReminderPriority.Normal, entry.Priority);
        Assert.Equal(MissedReminderAction.Skip, entry.Action);
    }

    [Fact]
    public void ConvertToEntry_DefaultsPriorityAndActionWhenSegmentsAreMissing()
    {
        var grainId = GrainId.Create("test", "redis-default-missing");
        var payload = BuildPayloadWithoutEnums(grainId);

        var entry = InvokeConvertToEntry(payload);

        Assert.Equal(ReminderPriority.Normal, entry.Priority);
        Assert.Equal(MissedReminderAction.Skip, entry.Action);
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

    private static string BuildPayload(
        GrainId grainId,
        ReminderPriority priority,
        MissedReminderAction action,
        bool numericEnums)
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
            TimeSpan.FromSeconds(10).ToString(),
            "*/5 * * * * *",
            nextDueUtc.ToString("O", CultureInfo.InvariantCulture),
            lastFireUtc.ToString("O", CultureInfo.InvariantCulture),
            priorityToken,
            actionToken
        };

        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static string BuildPayloadWithoutEnums(GrainId grainId)
        => BuildPayloadWithCustomEnums(grainId, includeEnums: false);

    private static string BuildPayloadWithCustomEnums(GrainId grainId, object priorityToken, object actionToken)
        => BuildPayloadWithCustomEnums(grainId, includeEnums: true, priorityToken, actionToken);

    private static string BuildPayloadWithCustomTimestamps(GrainId grainId, string startAtText, string nextDueUtcText, string lastFireUtcText)
    {
        var grainHash = grainId.GetUniformHashCode().ToString("X8", CultureInfo.InvariantCulture);
        var segments = new object[]
        {
            grainHash,
            grainId.ToString(),
            "reminder",
            "etag",
            startAtText,
            TimeSpan.FromSeconds(10).ToString("c", CultureInfo.InvariantCulture),
            "*/5 * * * * *",
            nextDueUtcText,
            lastFireUtcText,
            (int)ReminderPriority.Normal,
            (int)MissedReminderAction.Skip
        };

        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static string BuildPayloadWithCustomEnums(GrainId grainId, bool includeEnums, object priorityToken = null, object actionToken = null)
    {
        var startAt = DateTime.UtcNow;
        var nextDueUtc = startAt.AddSeconds(1);
        var lastFireUtc = startAt;
        var grainHash = grainId.GetUniformHashCode().ToString("X8", CultureInfo.InvariantCulture);
        var segments = new List<object>
        {
            grainHash,
            grainId.ToString(),
            "reminder",
            "etag",
            startAt.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(10).ToString("c", CultureInfo.InvariantCulture),
            "*/5 * * * * *",
            nextDueUtc.ToString("O", CultureInfo.InvariantCulture),
            lastFireUtc.ToString("O", CultureInfo.InvariantCulture)
        };

        if (includeEnums)
        {
            segments.Add(priorityToken);
            segments.Add(actionToken);
        }

        return JsonConvert.SerializeObject(segments)[1..^1];
    }

    private static ReminderEntry InvokeConvertToEntry(string payload)
    {
        var method = typeof(RedisReminderTable).GetMethod(
            "ConvertToEntry",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object[] { payload });
        Assert.NotNull(result);
        return (ReminderEntry)result!;
    }

    private static (string ETag, string Payload) InvokeConvertFromEntry(ReminderEntry entry)
    {
        var table = new RedisReminderTable(
            NullLogger<RedisReminderTable>.Instance,
            Options.Create(new ClusterOptions { ClusterId = "cluster", ServiceId = "service" }),
            Options.Create(new RedisReminderTableOptions()));

        var method = typeof(RedisReminderTable).GetMethod(
            "ConvertFromEntry",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(table, new object[] { entry });
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
