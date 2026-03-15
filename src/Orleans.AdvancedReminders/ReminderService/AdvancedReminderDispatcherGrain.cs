using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal interface IAdvancedReminderDispatcherGrain : IGrainWithStringKey, IDurableJobHandler
{
}

internal sealed class AdvancedReminderDispatcherGrain(IReminderService reminderService) : Grain, IAdvancedReminderDispatcherGrain
{
    private readonly IReminderService _reminderService = reminderService;

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (!AdvancedReminderService.TryGetReminderMetadata(context.Job.Metadata, out var grainId, out var reminderName, out var eTag))
        {
            return;
        }

        await _reminderService.ProcessDueReminderAsync(grainId, reminderName, eTag, cancellationToken);
    }
}
