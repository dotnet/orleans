using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Core;

internal interface IDashboardRemindersGrain : IGrainWithIntegerKey
{
    Task<Immutable<ReminderResponse>> GetReminders(int pageNumber, int pageSize);
}
