using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;

using StackExchange.Redis;

namespace Orleans.AdvancedReminders.Redis;

internal partial class RedisReminderTable
{
    internal static ReminderEntry ConvertToEntry(RedisValue reminderValue)
    {
        var byteCount = reminderValue.GetByteCount();
        var payload = ArrayPool<byte>.Shared.Rent(byteCount + 2);
        try
        {
            payload[0] = (byte)'[';
            reminderValue.CopyTo(payload.AsSpan(1, byteCount));
            payload[byteCount + 1] = (byte)']';
            var reader = new Utf8JsonReader(payload.AsSpan(0, byteCount + 2));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new FormatException("Reminder payload is not a JSON array.");
            }

            _ = ReadRequiredString(ref reader, 0); // Grain hash, used by the Redis lexicographical index.
            var entry = new ReminderEntry
            {
                GrainId = GrainId.Parse(ReadRequiredString(ref reader, 1)),
                ReminderName = ReadRequiredString(ref reader, 2),
                ETag = ReadRequiredString(ref reader, 3),
                StartAt = ReadRequiredDateTime(ref reader, 4),
                Period = ReadRequiredTimeSpan(ref reader, 5),
                CronExpression = ReadNullableString(ref reader),
                NextDueUtc = ReadNullableDateTime(ref reader),
                LastFireUtc = ReadNullableDateTime(ref reader),
                Action = ReadMissedReminderAction(ref reader),
                CronTimeZoneId = ReadNullableString(ref reader),
                ScheduleId = ReadNullableString(ref reader),
                JobId = ReadNullableString(ref reader),
                JobShardId = ReadNullableString(ref reader),
            };

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                reader.Skip();
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new FormatException("Reminder payload is not a complete JSON array.");
            }

            return entry;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private static string ReadRequiredString(ref Utf8JsonReader reader, int index)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new FormatException($"Reminder payload is missing segment {index}.");
        }

        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? throw new FormatException($"Reminder payload segment {index} is null."),
            JsonTokenType.Null => throw new FormatException($"Reminder payload segment {index} is null."),
            _ => throw new FormatException($"Reminder payload segment {index} must be a string."),
        };
    }

    private static string ReadNullableString(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray or JsonTokenType.Null)
        {
            return string.Empty;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            return string.Empty;
        }

        var value = reader.GetString();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private static DateTime ReadRequiredDateTime(ref Utf8JsonReader reader, int index)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new FormatException($"Reminder payload is missing segment {index}.");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new FormatException($"Reminder payload segment {index} must be a timestamp string.");
        }

        if (!reader.ValueIsEscaped
            && Utf8Parser.TryParse(reader.ValueSpan, out DateTime result, out var bytesConsumed, 'O')
            && bytesConsumed == reader.ValueSpan.Length)
        {
            return result;
        }

        return DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static TimeSpan ReadRequiredTimeSpan(ref Utf8JsonReader reader, int index)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
        {
            throw new FormatException($"Reminder payload is missing segment {index}.");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new FormatException($"Reminder payload segment {index} must be a duration string.");
        }

        if (!reader.ValueIsEscaped
            && Utf8Parser.TryParse(reader.ValueSpan, out TimeSpan result, out var bytesConsumed, 'c')
            && bytesConsumed == reader.ValueSpan.Length)
        {
            return result;
        }

        return TimeSpan.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
    }

    private static DateTime? ReadNullableDateTime(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray or JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.IsEmpty)
        {
            return null;
        }

        if (!reader.ValueIsEscaped
            && Utf8Parser.TryParse(reader.ValueSpan, out DateTime result, out var bytesConsumed, 'O')
            && bytesConsumed == reader.ValueSpan.Length)
        {
            return result;
        }

        var value = reader.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static MissedReminderAction ReadMissedReminderAction(ref Utf8JsonReader reader)
    {
        if (!TryReadInt32(ref reader, out var value))
        {
            return MissedReminderAction.Skip;
        }

        return ParseAction(value);
    }

    private static bool TryReadInt32(ref Utf8JsonReader reader, out int value)
    {
        value = default;
        if (!reader.Read() || reader.TokenType is JsonTokenType.EndArray or JsonTokenType.Null)
        {
            return false;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetInt32(out value);
        }

        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static MissedReminderAction ParseAction(int value) => value switch
    {
        (int)MissedReminderAction.FireImmediately => MissedReminderAction.FireImmediately,
        (int)MissedReminderAction.Skip => MissedReminderAction.Skip,
        (int)MissedReminderAction.Notify => MissedReminderAction.Notify,
        _ => MissedReminderAction.Skip,
    };

    private (RedisValue eTag, RedisValue value) ConvertFromEntry(ReminderEntry entry)
    {
        string grainHash = entry.GrainId.GetUniformHashCode().ToString("X8");
        string eTag = Guid.NewGuid().ToString("N");
        return (eTag, SerializeEntry(entry, grainHash, eTag));
    }

    private static string SerializeSegments(IReadOnlyList<string> segments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < segments.Count; i++)
            {
                writer.WriteStringValue(segments[i]);
            }

            writer.WriteEndArray();
        }

        return TrimArrayDelimiters(buffer.WrittenSpan);
    }

    private static string SerializeEntry(ReminderEntry entry, string grainHash, string eTag)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(grainHash);
            writer.WriteStringValue(entry.GrainId.ToString());
            writer.WriteStringValue(entry.ReminderName);
            writer.WriteStringValue(eTag);
            WriteDateTimeValue(writer, entry.StartAt);
            WriteTimeSpanValue(writer, entry.Period);
            writer.WriteStringValue(entry.CronExpression ?? string.Empty);
            WriteNullableDateTimeValue(writer, entry.NextDueUtc);
            WriteNullableDateTimeValue(writer, entry.LastFireUtc);
            writer.WriteNumberValue((int)entry.Action);
            writer.WriteStringValue(entry.CronTimeZoneId ?? string.Empty);
            writer.WriteStringValue(entry.ScheduleId ?? string.Empty);
            writer.WriteStringValue(entry.JobId ?? string.Empty);
            writer.WriteStringValue(entry.JobShardId ?? string.Empty);
            writer.WriteEndArray();
        }

        return TrimArrayDelimiters(buffer.WrittenSpan);
    }

    private static void WriteDateTimeValue(Utf8JsonWriter writer, DateTime value)
    {
        Span<char> formatted = stackalloc char[33];
        if (!value.TryFormat(formatted, out var charsWritten, "O", CultureInfo.InvariantCulture))
        {
            throw new FormatException("Could not format reminder timestamp.");
        }

        writer.WriteStringValue(formatted[..charsWritten]);
    }

    private static void WriteNullableDateTimeValue(Utf8JsonWriter writer, DateTime? value)
    {
        if (value is { } timestamp)
        {
            WriteDateTimeValue(writer, timestamp);
        }
        else
        {
            writer.WriteStringValue(string.Empty);
        }
    }

    private static void WriteTimeSpanValue(Utf8JsonWriter writer, TimeSpan value)
    {
        Span<char> formatted = stackalloc char[26];
        if (!value.TryFormat(formatted, out var charsWritten, "c", CultureInfo.InvariantCulture))
        {
            throw new FormatException("Could not format reminder period.");
        }

        writer.WriteStringValue(formatted[..charsWritten]);
    }

    private static string TrimArrayDelimiters(ReadOnlySpan<byte> json)
    {
        if (json.Length < 2 || json[0] != (byte)'[' || json[^1] != (byte)']')
        {
            throw new FormatException("Reminder payload is not a JSON array.");
        }

        return Encoding.UTF8.GetString(json[1..^1]);
    }

    private readonly struct ReminderEntryLogValue(ReminderEntry entry)
    {
        public override string ToString() => entry.ToString() ?? string.Empty;
    }
}
