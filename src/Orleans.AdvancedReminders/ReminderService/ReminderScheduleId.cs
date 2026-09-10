namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal static class ReminderScheduleId
{
    private const string AttributePrefix = "a1:";
    private const string RegistrationPrefix = "r1:";

    public static string Create(string? declarationId = null)
    {
        var prefix = declarationId is null ? RegistrationPrefix : AttributePrefix;
        var registrationId = declarationId ?? Guid.NewGuid().ToString("N");
        return Create(prefix, registrationId);
    }

    public static string RotateOccurrence(string scheduleId)
    {
        var registrationEnd = GetRegistrationEnd(scheduleId);
        if (registrationEnd < 0)
        {
            return Create(RegistrationPrefix, scheduleId);
        }

        var prefixLength = registrationEnd + 1;
        return string.Create(
            prefixLength + 32,
            (ScheduleId: scheduleId, PrefixLength: prefixLength, OccurrenceId: Guid.NewGuid()),
            static (destination, state) =>
            {
                state.ScheduleId.AsSpan(0, state.PrefixLength).CopyTo(destination);
                _ = state.OccurrenceId.TryFormat(destination[state.PrefixLength..], out _, "N");
            });
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
            prefix.Length + registrationId.Length + 1 + 32,
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
