using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAttributeReminderService
{
    Task<IGrainReminder> ReconcileReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        ReminderPriority priority,
        MissedReminderAction action,
        string declarationId);
}

internal static class AttributeReminderRegistration
{
    private const int DeclarationHashLength = 12;

    public static string GetDeclarationId(
        ReminderSchedule schedule,
        ReminderPriority priority,
        MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var declaration = schedule.Kind switch
        {
            ReminderScheduleKind.Interval => string.Create(
                CultureInfo.InvariantCulture,
                $"v1|interval|{schedule.DueTime?.Ticks}|{schedule.DueAtUtc?.Ticks}|{schedule.Period?.Ticks}|{schedule.IsOneShot}|{(int)priority}|{(int)action}"),
            ReminderScheduleKind.Cron => string.Create(
                CultureInfo.InvariantCulture,
                $"v1|cron|{schedule.CronExpression?.Trim()}|{schedule.CronTimeZoneId?.Trim()}|{(int)priority}|{(int)action}"),
            _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule.Kind, "Unsupported reminder schedule kind."),
        };

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(declaration), hash);
        return Convert.ToBase64String(hash[..DeclarationHashLength])
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
