using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;

namespace Orleans.DurableReminders.Runtime.ReminderService;

internal interface IDurableReminderDispatcherGrain : IGrainWithStringKey, IDurableJobHandler
{
}

internal sealed class DurableReminderDispatcherGrain(IReminderService reminderService) : Grain, IDurableReminderDispatcherGrain
{
    private readonly IReminderService _reminderService = reminderService;

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (!DurableReminderService.TryGetReminderMetadata(context.Job.Metadata, out var grainId, out var reminderName, out var eTag))
        {
            return;
        }

        await _reminderService.ProcessDueReminderAsync(grainId, reminderName, eTag, cancellationToken);
    }
}
