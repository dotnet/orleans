using System.Threading;
using System.Threading.Tasks;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Core;

internal interface IAdvancedReminderDashboardSource
{
    Task<long> GetCountAsync(CancellationToken cancellationToken);

    Task<(AdvancedReminderInfo[] Reminders, string? ContinuationToken)> ReadPageAsync(
        int pageSize, string? continuationToken, CancellationToken cancellationToken);
}
