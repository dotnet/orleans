using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Timers;

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
        => RegisterOrUpdateReminder((IGrainBase)grain, reminderName, dueTime, period);

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
    {
#if NET6_0_OR_GREATER
    ArgumentNullException.ThrowIfNull(grain, nameof(reminderName));
#else
        if (reminderName is null)
        {
            throw new ArgumentNullException(nameof(reminderName));
        }
#endif

        if (string.IsNullOrWhiteSpace(reminderName))
        {
            throw new ArgumentNullException(nameof(reminderName));
        }

        if (grain is not IRemindable)
        {
            throw new InvalidOperationException($"Grain {grain.GrainContext.GrainId} is not '{nameof(IRemindable)}'. A grain should implement {nameof(IRemindable)} to use the persistent reminder service");
        }

        EnsureRuntime();

        return GetReminderRegistry(grain)
            .RegisterOrUpdateReminder(grain.GrainContext.GrainId, reminderName, dueTime, period);
    }

    /// <summary>
    /// Unregisters a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminder">Reminder to unregister.</param>
    /// <returns>Completion promise for this operation.</returns>
    public static Task UnregisterReminder(this Grain grain, IGrainReminder reminder) => UnregisterReminder((IGrainBase)grain, reminder);

    /// <summary>
    /// Unregisters a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminder">Reminder to unregister.</param>
    /// <returns>Completion promise for this operation.</returns>
    public static Task UnregisterReminder(this IGrainBase grain, IGrainReminder reminder)
    {
#if NET6_0_OR_GREATER
    ArgumentNullException.ThrowIfNull(grain, nameof(grain));
#else
        if (reminder is null)
        {
            throw new ArgumentNullException(nameof(reminder));
        }
#endif

        EnsureRuntime();

        return GetReminderRegistry(grain)
            .UnregisterReminder(grain.GrainContext.GrainId, reminder);
    }

    /// <summary>
    /// Returns a previously registered reminder.
    /// </summary>
    /// <param name="grain">The grain instance.</param>
    /// <param name="reminderName">Reminder to return</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> GetReminder(this Grain grain, string reminderName) => GetReminder((IGrainBase)grain, reminderName);

    /// <summary>
    /// Returns a previously registered reminder.
    /// </summary>
    /// <param name="grain">A grain.</param>
    /// <param name="reminderName">Reminder to return</param>
    /// <returns>Promise for Reminder handle.</returns>
    public static Task<IGrainReminder> GetReminder(this IGrainBase grain, string reminderName)
    {
#if NET6_0_OR_GREATER
    ArgumentNullException.ThrowIfNull(grain, nameof(grain));
#else
        if (grain is null)
        {
            throw new ArgumentNullException(nameof(grain));
        }
#endif

        if (string.IsNullOrWhiteSpace(reminderName))
        {
            throw new ArgumentNullException(nameof(reminderName));
        }

        EnsureRuntime();

        return GetReminderRegistry(grain)
            .GetReminder(grain.GrainContext.GrainId, reminderName);
    }

    /// <summary>
    /// Returns a list of all reminders registered by the grain.
    /// </summary>
    /// <returns>Promise for list of Reminders registered for this grain.</returns>
    public static Task<List<IGrainReminder>> GetReminders(this Grain grain) => GetReminders((IGrainBase)grain);

    /// <summary>
    /// Returns a list of all reminders registered by the grain.
    /// </summary>
    /// <returns>Promise for list of Reminders registered for this grain.</returns>
    public static Task<List<IGrainReminder>> GetReminders(this IGrainBase grain)
    {
#if NET6_0_OR_GREATER
    ArgumentNullException.ThrowIfNull(grain, nameof(grain));
#else
        if (grain is null)
        {
            throw new ArgumentNullException(nameof(grain));
        }
#endif
        EnsureRuntime();

        return GetReminderRegistry(grain).GetReminders(grain.GrainContext.GrainId);
    }

    /// <summary>
    /// Gets the <see cref="IReminderService"/>.
    /// </summary>
    private static IReminderRegistry GetReminderRegistry(IGrainBase grain) => grain.GrainContext.ActivationServices.GetRequiredService<IReminderRegistry>();

    private static void EnsureRuntime()
    {
        if (RuntimeContext.Current is null)
        {
            throw new InvalidOperationException("Attempted to access grain from a non-grain context, such as a background thread, which is invalid."
                + " Ensure that you are only accessing grain functionality from within the context of a grain.");
        }
    }
}