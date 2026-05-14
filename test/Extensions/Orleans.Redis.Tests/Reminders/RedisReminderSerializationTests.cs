using System.Globalization;
using Newtonsoft.Json;
using Orleans.Reminders.Redis;
using TestExtensions;
using Xunit;
using static VerifyXunit.Verifier;

namespace Tester.Redis.Reminders;

[TestCategory("BVT"), TestCategory("Reminders")]
public class RedisReminderSerializationTests
{
    private static readonly string[] RepresentativeReminderSegments =
    [
        "4D71E4A2",
        "clientaccount/4642c88644b837d308c9e139e4af937b",
        "Balance",
        "0733744d-8a76-4816-a6dd-b0158e2135cc",
        "2026-05-13T18:55:08.8620861Z",
        "10.00:00:00"
    ];

    private static readonly string[] TrickyReminderSegments =
    [
        "0000002A",
        "client+account/key<&>é漢\"\\/\n",
        "Balance+<&>é漢\"\\",
        "etag+<&>é漢\"\\",
        "2026-05-13T18:55:08.8620861Z",
        "00:00:01"
    ];

    [Fact]
    public Task ReminderValue_MatchesNewtonsoftDefaultSnapshot()
    {
        var value = RedisReminderSerializer.SerializeMember(RepresentativeReminderSegments);
        AssertMatchesLegacyNewtonsoftDefault(RepresentativeReminderSegments, value);
        return Verify(value, extension: "txt").UseDirectory("snapshots");
    }

    [Fact]
    public Task ReminderValue_WithTrickyCharacters_MatchesNewtonsoftDefaultSnapshot()
    {
        var value = RedisReminderSerializer.SerializeMember(TrickyReminderSegments);
        AssertMatchesLegacyNewtonsoftDefault(TrickyReminderSegments, value);
        return Verify(value, extension: "txt").UseDirectory("snapshots");
    }

    [Fact]
    public Task ReminderFilter_MatchesNewtonsoftDefaultSnapshot()
    {
        var filterSegments = TrickyReminderSegments[..3];
        var filter = RedisReminderSerializer.GetFilter(filterSegments);
        var legacyPrefix = SerializeWithLegacyNewtonsoftDefault(filterSegments);

        Assert.Equal($"{legacyPrefix},\"", filter.From);
        Assert.Equal($"{legacyPrefix},#", filter.To);
        return Verify($"from: {filter.From}\nto: {filter.To}", extension: "txt").UseDirectory("snapshots");
    }

    [Fact]
    public Task ReminderValue_IgnoresNewtonsoftDefaultSettings()
    {
        var previousDefaultSettings = JsonConvert.DefaultSettings;
        JsonConvert.DefaultSettings = CreateHostileJsonSettings;
        try
        {
            var value = RedisReminderSerializer.SerializeMember(TrickyReminderSegments);
            AssertMatchesLegacyNewtonsoftDefault(TrickyReminderSegments, value);
            return Verify(value, extension: "txt").UseDirectory("snapshots");
        }
        finally
        {
            JsonConvert.DefaultSettings = previousDefaultSettings;
        }
    }

    private static void AssertMatchesLegacyNewtonsoftDefault(string[] segments, string value)
    {
        Assert.Equal(SerializeWithLegacyNewtonsoftDefault(segments), value);
    }

    private static string SerializeWithLegacyNewtonsoftDefault(string[] segments)
    {
        var previousDefaultSettings = JsonConvert.DefaultSettings;
        JsonConvert.DefaultSettings = null;
        try
        {
            return JsonConvert.SerializeObject(segments)[1..^1];
        }
        finally
        {
            JsonConvert.DefaultSettings = previousDefaultSettings;
        }
    }

    private static JsonSerializerSettings CreateHostileJsonSettings()
    {
        return new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture,
            Formatting = Formatting.Indented,
            StringEscapeHandling = StringEscapeHandling.EscapeHtml,
            Converters = { new HostileStringConverter() }
        };
    }

    private sealed class HostileStringConverter : JsonConverter<string>
    {
        public override void WriteJson(JsonWriter writer, string value, JsonSerializer serializer)
        {
            writer.WriteValue($"converted:{value}");
        }

        public override string ReadJson(JsonReader reader, Type objectType, string existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return (string)reader.Value;
        }
    }
}
