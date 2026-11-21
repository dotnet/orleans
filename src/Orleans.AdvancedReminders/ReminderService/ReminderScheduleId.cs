using System.Globalization;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal static class ReminderScheduleId
{
    private const string AttributePrefix = "a1:";
    private const string RegistrationPrefix = "r1:";
    private const int OccurrenceIdLength = 32;
    private const int DeliveryDueSuffixLength = 17;

    public static string Create(string? declarationId = null)
    {
        var prefix = declarationId is null ? RegistrationPrefix : AttributePrefix;
        var registrationId = declarationId ?? Guid.NewGuid().ToString("N");
        return Create(prefix, registrationId);
    }

    public static string RotateOccurrence(string scheduleId, DateTime? deliveryDueUtc = null)
    {
        var registrationEnd = GetRegistrationEnd(scheduleId);
        string occurrence;
        if (registrationEnd < 0)
        {
            occurrence = Create(RegistrationPrefix, scheduleId);
        }
        else
        {
            var prefixLength = registrationEnd + 1;
            occurrence = string.Create(
                prefixLength + OccurrenceIdLength,
                (ScheduleId: scheduleId, PrefixLength: prefixLength, OccurrenceId: Guid.NewGuid()),
                static (destination, state) =>
                {
                    state.ScheduleId.AsSpan(0, state.PrefixLength).CopyTo(destination);
                    _ = state.OccurrenceId.TryFormat(destination[state.PrefixLength..], out _, "N");
                });
        }

        // The carrier deadline belongs to this occurrence and survives repair using the same
        // opaque token across providers. Older readers still recognize the registration prefix.
        return deliveryDueUtc is { } due
            ? string.Create(CultureInfo.InvariantCulture, $"{occurrence}:{due.Ticks:X16}")
            : occurrence;
    }

    public static DateTime? GetDeliveryDueUtc(string scheduleId)
    {
        var registrationEnd = GetRegistrationEnd(scheduleId);
        var suffix = registrationEnd + 1 + OccurrenceIdLength;
        if (registrationEnd < 0 || scheduleId.Length - suffix != DeliveryDueSuffixLength || scheduleId[suffix] != ':'
            || !long.TryParse(scheduleId.AsSpan(suffix + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    public static string GetRegistrationId(string scheduleId)
    {
        var prefixLength = GetKnownPrefixLength(scheduleId);
        if (prefixLength == 0)
        {
            return scheduleId;
        }

        var registrationEnd = scheduleId.IndexOf(':', prefixLength);
        return registrationEnd > prefixLength
            ? scheduleId[prefixLength..registrationEnd]
            : scheduleId;
    }

    public static bool HasAttributeDeclaration(string scheduleId, string declarationId)
    {
        var registrationEnd = GetRegistrationEnd(scheduleId, AttributePrefix);
        return registrationEnd == AttributePrefix.Length + declarationId.Length
            && scheduleId.AsSpan(AttributePrefix.Length, declarationId.Length).SequenceEqual(declarationId);
    }

    private static string Create(string prefix, string registrationId)
        => string.Create(
            prefix.Length + registrationId.Length + 1 + OccurrenceIdLength,
            (Prefix: prefix, RegistrationId: registrationId, OccurrenceId: Guid.NewGuid()),
            static (destination, state) =>
            {
                state.Prefix.CopyTo(destination);
                state.RegistrationId.CopyTo(destination[state.Prefix.Length..]);
                var occurrenceSeparator = state.Prefix.Length + state.RegistrationId.Length;
                destination[occurrenceSeparator] = ':';
                _ = state.OccurrenceId.TryFormat(destination[(occurrenceSeparator + 1)..], out _, "N");
            });

    private static int GetRegistrationEnd(string scheduleId)
    {
        var prefixLength = GetKnownPrefixLength(scheduleId);
        return prefixLength == 0 ? -1 : scheduleId.IndexOf(':', prefixLength);
    }

    private static int GetRegistrationEnd(string scheduleId, string prefix)
        => scheduleId.StartsWith(prefix, StringComparison.Ordinal)
            ? scheduleId.IndexOf(':', prefix.Length)
            : -1;

    private static int GetKnownPrefixLength(string scheduleId)
    {
        if (scheduleId.StartsWith(AttributePrefix, StringComparison.Ordinal))
        {
            return AttributePrefix.Length;
        }

        return scheduleId.StartsWith(RegistrationPrefix, StringComparison.Ordinal)
            ? RegistrationPrefix.Length
            : 0;
    }
}
