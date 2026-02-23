#nullable enable
using System;

namespace Orleans.Reminders.Cron.Internal;

internal static class ReminderCronParser
{
    public static CronExpression Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return CronExpression.Parse(expression, DetectFormat(expression));
    }

    public static CronFormat DetectFormat(string expression)
    {
        var trimmed = expression.AsSpan().Trim();
        if (trimmed is ['@', ..])
        {
            return CronFormat.Standard;
        }

        var fieldCount = 0;
        var inToken = false;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                inToken = false;
                continue;
            }

            if (!inToken)
            {
                fieldCount++;
                inToken = true;
            }
        }

        return fieldCount switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => throw new CronFormatException($"The given cron expression has an invalid format. Expected 5 or 6 fields, but got {fieldCount}.")
        };
    }
}
