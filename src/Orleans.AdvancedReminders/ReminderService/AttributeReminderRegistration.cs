using System;
using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAttributeReminderService
{
    Task<IGrainReminder> ReconcileReminder(
        GrainId grainId,
        string reminderName,
        ReminderSchedule schedule,
        DurableJobPriority priority,
        MissedReminderAction action,
        string declarationId);
}

internal static class AttributeReminderRegistration
{
    private const int DeclarationHashLength = 12;

    public static string GetDeclarationId(
        ReminderSchedule schedule,
        DurableJobPriority priority,
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

        var byteCount = Encoding.UTF8.GetByteCount(declaration);
        byte[]? rentedBuffer = null;
        Span<byte> utf8 = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(byteCount));
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            _ = Encoding.UTF8.GetBytes(declaration, utf8);
            SHA256.HashData(utf8[..byteCount], hash);
        }
        finally
        {
            if (rentedBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        Span<char> encoded = stackalloc char[16];
        if (!Convert.TryToBase64Chars(hash[..DeclarationHashLength], encoded, out var charsWritten))
        {
            throw new InvalidOperationException("Could not encode reminder declaration id.");
        }

        for (var index = 0; index < charsWritten; index++)
        {
            encoded[index] = encoded[index] switch
            {
                '+' => '-',
                '/' => '_',
                _ => encoded[index],
            };
        }

        return new string(encoded[..charsWritten]);
    }
}
