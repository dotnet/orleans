using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.AdvancedReminders;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Model;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdvancedReminderTable = Orleans.AdvancedReminders.IReminderTable;

namespace Orleans.Dashboard;

internal sealed class AdvancedReminderDashboardSource(
    AdvancedReminderTable table, IReminderManagementGrain management) : IAdvancedReminderDashboardSource
{
    public Task<long> GetCountAsync(CancellationToken cancellationToken) => table.GetCountAsync(cancellationToken);

    public async Task<(AdvancedReminderInfo[] Reminders, string? ContinuationToken)> ReadPageAsync(
        int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = await management.ListAllAsync(pageSize, continuationToken).WaitAsync(cancellationToken);
        return (page.Reminders.Select(ToAdvancedReminderInfo).ToArray(), page.ContinuationToken);
    }

    private static AdvancedReminderInfo ToAdvancedReminderInfo(AdvancedReminderEntry entry)
    {
        return new AdvancedReminderInfo
        {
            PrimaryKey = entry.GrainId.Key.ToString(),
            GrainReference = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            CronExpression = entry.CronExpression,
            CronTimeZoneId = entry.CronTimeZoneId,
            NextDueUtc = entry.NextDueUtc,
            LastFireUtc = entry.LastFireUtc,
            MissedAction = entry.Action.ToString(),
        };
    }
}
