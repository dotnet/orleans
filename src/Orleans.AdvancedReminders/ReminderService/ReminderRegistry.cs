using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.AdvancedReminders.Timers;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class ReminderRegistry(
    IServiceProvider serviceProvider,
    IOptions<ReminderOptions> options,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider timeProvider) : IReminderRegistry
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ReminderOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider;

    public Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId callingGrainId,
        string reminderName,
        ReminderSchedule schedule,
        Runtime.MissedReminderAction action)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ReminderValidation.Validate(_options, reminderName, schedule, action, _timeProvider.GetUtcNow().UtcDateTime);
        return GetReminderService().RegisterOrUpdateReminder(callingGrainId, reminderName, schedule, action);
    }

    public Task UnregisterReminder(GrainId callingGrainId, IGrainReminder reminder)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return GetReminderService().UnregisterReminder(reminder);
    }

    public Task<IGrainReminder?> GetReminder(GrainId callingGrainId, string reminderName)
    {
        if (string.IsNullOrWhiteSpace(reminderName))
        {
            throw new ArgumentException("Cannot use null or empty name for the reminder", nameof(reminderName));
        }

        return GetReminderService().GetReminder(callingGrainId, reminderName);
    }

    public Task<List<IGrainReminder>> GetReminders(GrainId callingGrainId) => GetReminderService().GetReminders(callingGrainId);

    private IReminderService GetReminderService()
        => _serviceProvider.GetRequiredService<IReminderService>();
}
