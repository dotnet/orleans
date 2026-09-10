using System.Globalization;
using Amazon.DynamoDBv2.Model;

namespace Orleans.AdvancedReminders.DynamoDB;

internal sealed partial class DynamoDBReminderTable
{
    private ReminderEntry Resolve(Dictionary<string, AttributeValue> item)
    {
        return new ReminderEntry
        {
            ETag = ReadETag(item[ETAG_PROPERTY_NAME]),
            GrainId = GrainId.Parse(item[GRAIN_REFERENCE_PROPERTY_NAME].S),
            Period = TimeSpan.Parse(item[PERIOD_PROPERTY_NAME].S, CultureInfo.InvariantCulture),
            CronExpression = ReadOptionalString(item, CRON_EXPRESSION_PROPERTY_NAME),
            CronTimeZoneId = ReadOptionalString(item, CRON_TIME_ZONE_ID_PROPERTY_NAME),
            NextDueUtc = ReadOptionalDateTime(item, NEXT_DUE_UTC_PROPERTY_NAME),
            LastFireUtc = ReadOptionalDateTime(item, LAST_FIRE_UTC_PROPERTY_NAME),
            ScheduleId = ReadOptionalString(item, SCHEDULE_ID_PROPERTY_NAME),
            JobId = ReadOptionalString(item, JOB_ID_PROPERTY_NAME),
            JobShardId = ReadOptionalString(item, JOB_SHARD_ID_PROPERTY_NAME),
            Action = ReadAction(item),
            ReminderName = item[REMINDER_NAME_PROPERTY_NAME].S,
            StartAt = DateTime.Parse(item[START_TIME_PROPERTY_NAME].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    private static string ReadOptionalString(Dictionary<string, AttributeValue> item, string propertyName)
        => item.TryGetValue(propertyName, out var value) ? value.S ?? string.Empty : string.Empty;

    private static string ReadETag(AttributeValue value)
        => !string.IsNullOrEmpty(value.S) ? value.S : value.N;

    internal static bool TryCreateETagValue(string eTag, out AttributeValue value)
    {
        if (Guid.TryParseExact(eTag, "N", out _))
        {
            value = new AttributeValue(eTag);
            return true;
        }

        if (long.TryParse(eTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyETag)
            && string.Equals(eTag, legacyETag.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            value = new AttributeValue { N = eTag };
            return true;
        }

        value = null!;
        return false;
    }

    private static AttributeValue CreateETagValue(string eTag)
        => TryCreateETagValue(eTag, out var value)
            ? value
            : throw new FormatException($"ETag '{eTag}' is neither a GUID nor a legacy numeric ETag.");

    private static DateTime? ReadOptionalDateTime(Dictionary<string, AttributeValue> item, string propertyName)
    {
        if (!item.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value.S))
        {
            return null;
        }

        return DateTime.Parse(value.S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static MissedReminderAction ReadAction(Dictionary<string, AttributeValue> item)
    {
        if (!TryReadInt32(item, ACTION_PROPERTY_NAME, out var value))
        {
            return MissedReminderAction.Skip;
        }

        return ParseAction(value);
    }

    private static bool TryReadInt32(Dictionary<string, AttributeValue> item, string propertyName, out int value)
    {
        value = default;
        return item.TryGetValue(propertyName, out var attributeValue)
            && !string.IsNullOrWhiteSpace(attributeValue.N)
            && int.TryParse(attributeValue.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static MissedReminderAction ParseAction(int value) => value switch
    {
        (int)MissedReminderAction.FireImmediately => MissedReminderAction.FireImmediately,
        (int)MissedReminderAction.Skip => MissedReminderAction.Skip,
        (int)MissedReminderAction.Notify => MissedReminderAction.Notify,
        _ => MissedReminderAction.Skip,
    };
}
