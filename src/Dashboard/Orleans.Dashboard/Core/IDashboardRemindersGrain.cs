using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Core;

[Alias("Orleans.Dashboard.Core.IDashboardRemindersGrain")]
internal interface IDashboardRemindersGrain : IGrainWithIntegerKey
{
    [Alias("GetReminders")]
    Task<Immutable<ReminderResponse>> GetReminders(int pageNumber, int pageSize);
}
