using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Timers;

#nullable enable
namespace Orleans;

/// <summary>
/// Extension methods for accessing reminders from a <see cref="Grain"/> or <see cref="IGrainBase"/> implementation.
/// </summary>
public static class GrainReminderExtensions
{
    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain.
    /// The grain must implement the <c>Orleans.IRemindable</c> interface, and reminders for this grain will be sent to the <c>ReceiveReminder</c> callback method.
    /// If the current grain is deactivated when the timer fires, a new activation of this grain will be created to receive this reminder.
    /// If an existing reminder with the same name already exists, that reminder will be overwritten with this new reminder.
    /// Reminders will always be received by one activation of this grain, even if multiple activations exist for this grain.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder</param>
    /// <param name="dueTime">Due time for this reminder</param>
    /// <param name="period">Frequency period for this reminder</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this Grain grain, string reminderName, TimeSpan dueTime, TimeSpan period)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, dueTime, period);

    /// <summary>
    /// Registers a persistent, reliable reminder to send regular notifications (reminders) to the grain.
    /// The grain must implement the <c>Orleans.IRemindable</c> interface, and reminders for this grain will be sent to the <c>ReceiveReminder</c> callback method.
    /// If the current grain is deactivated when the timer fires, a new activation of this grain will be created to receive this reminder.
    /// If an existing reminder with the same name already exists, that reminder will be overwritten with this new reminder.
    /// Reminders will always be received by one activation of this grain, even if multiple activations exist for this grain.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Name of this reminder</param>
    /// <param name="dueTime">Due time for this reminder</param>
    /// <param name="period">Frequency period for this reminder</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> RegisterOrUpdateReminder(this IGrainBase grain, string reminderName, TimeSpan dueTime, TimeSpan period)
        => RegisterOrUpdateReminder(grain is IRemindable, grain?.GrainContext, reminderName, dueTime, period);

    private static Task<IGrainReminder> RegisterOrUpdateReminder(bool remindable, IGrainContext? grainContext, string reminderName, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        if (string.IsNullOrWhiteSpace(reminderName)) throw new ArgumentNullException(nameof(reminderName));
        if (!remindable) throw new InvalidOperationException($"Grain {grainContext.GrainId} is not '{nameof(IRemindable)}'. A grain should implement {nameof(IRemindable)} to use the persistent reminder service");

        return GetReminderRegistry(grainContext).RegisterOrUpdateReminder(grainContext.GrainId, reminderName, dueTime, period);
    }

    /// <summary>
    /// Unregisters a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminder">Reminder to unregister.</param>
    /// <returns>Completion promise for this operation.</returns>
    public static Task UnregisterReminder(this Grain grain, IGrainReminder reminder) => UnregisterReminder(grain?.GrainContext, reminder);

    /// <summary>
    /// Unregisters a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminder">Reminder to unregister.</param>
    /// <returns>Completion promise for this operation.</returns>
    public static Task UnregisterReminder(this IGrainBase grain, IGrainReminder reminder) => UnregisterReminder(grain?.GrainContext, reminder);

    private static Task UnregisterReminder(IGrainContext? grainContext, IGrainReminder reminder)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        return GetReminderRegistry(grainContext).UnregisterReminder(grainContext.GrainId, reminder);
    }

    /// <summary>
    /// Returns a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Reminder to return</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> GetReminder(this Grain grain, string reminderName) => GetReminder(grain?.GrainContext, reminderName);

    /// <summary>
    /// Returns a previously registered reminder.
    /// </summary>
    /// <param name="grain">A grain.</param>
    /// <param name="reminderName">Reminder to return</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> GetReminder(this IGrainBase grain, string reminderName) => GetReminder(grain?.GrainContext, reminderName);

    private static Task<IGrainReminder> GetReminder(IGrainContext? grainContext, string reminderName)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        if (string.IsNullOrWhiteSpace(reminderName)) throw new ArgumentNullException(nameof(reminderName));

        return GetReminderRegistry(grainContext).GetReminder(grainContext.GrainId, reminderName);
    }

    /// <summary>
    /// Returns a list of all reminders registered by the grain.
    /// </summary>
    /// <returns>Promise for list of Reminders registered for this grain.</returns>
    public static Task<List<IGrainReminder>> GetReminders(this Grain grain) => GetReminders(grain?.GrainContext);

    /// <summary>
    /// Returns a list of all reminders registered by the grain.
    /// </summary>
    /// <returns>Promise for list of Reminders registered for this grain.</returns>
    public static Task<List<IGrainReminder>> GetReminders(this IGrainBase grain) => GetReminders(grain?.GrainContext);

    private static Task<List<IGrainReminder>> GetReminders(IGrainContext? grainContext)
    {
        ArgumentNullException.ThrowIfNull(grainContext, "grain");
        return GetReminderRegistry(grainContext).GetReminders(grainContext.GrainId);
    }

    /// <summary>
    /// Gets the <see cref="IReminderService"/>.
    /// </summary>
    private static IReminderRegistry GetReminderRegistry(IGrainContext grainContext)
    {
        if (RuntimeContext.Current is null) ThrowInvalidContext();
        return grainContext.ActivationServices.GetRequiredService<IReminderRegistry>();
    }

    private static void ThrowInvalidContext()
    {
        throw new InvalidOperationException("Attempted to access grain from a non-grain context, such as a background thread, which is invalid."
            + " Ensure that you are only accessing grain functionality from within the context of a grain.");
    }
}