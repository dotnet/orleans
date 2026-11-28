namespace Orleans.AdvancedReminders.Cron.Internal;

internal static class ReminderCronParser
{
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    public static CronSchedulePattern Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var expanded = ExpandMacro(expression.Trim());
        var fields = expanded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (5 or 6))
        {
            throw new ReminderCronParseException($"Expected 5 or 6 cron fields, received {fields.Length}.");
        }

        if (fields.Length == 5)
        {
            fields = ["0", .. fields];
        }

        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Contains('?', StringComparison.Ordinal) && (i is not (3 or 5) || fields[i] != "?"))
            {
                throw CronValueSet.Invalid(fields[i]);
            }
        }

        return new CronSchedulePattern(
            CronValueSet.Parse(fields[0], 0, 59),
            CronValueSet.Parse(fields[1], 0, 59),
            CronValueSet.Parse(fields[2], 0, 23),
            CronValueSet.Parse(fields[4], 1, 12, Months),
            new CronCalendarRule(fields[3], fields[5]),
            IsInterval(fields[0]) || IsInterval(fields[1]) || IsInterval(fields[2]));
    }

    public static bool IncludesSeconds(string expression)
        => ExpandMacro(expression.Trim()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 6;

    private static bool IsInterval(string field) => field.AsSpan().IndexOfAny('*', '/', '-') >= 0;

    private static string ExpandMacro(string expression)
    {
        if (!expression.StartsWith('@'))
        {
            return expression;
        }

        return expression.ToUpperInvariant() switch
        {
            "@YEARLY" or "@ANNUALLY" => "0 0 1 1 *",
            "@MONTHLY" => "0 0 1 * *",
            "@WEEKLY" => "0 0 * * 0",
            "@DAILY" or "@MIDNIGHT" => "0 0 * * *",
            "@HOURLY" => "0 * * * *",
            "@EVERY_MINUTE" => "* * * * *",
            "@EVERY_SECOND" => "* * * * * *",
            _ => expression,
        };
    }
}
