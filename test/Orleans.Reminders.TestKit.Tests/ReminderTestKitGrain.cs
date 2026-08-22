using Orleans.Runtime;

namespace Orleans.Reminders.TestKit.Tests;

/// <summary>
/// A minimal remindable grain used by the TestKit cluster integration tests.
/// </summary>
public interface IReminderTestKitGrain : IGrainWithGuidKey
{
    /// <summary>Registers or updates a reminder and returns its name.</summary>
    Task<string> RegisterReminderAsync(string reminderName, TimeSpan dueTime, TimeSpan period);

    /// <summary>Unregisters a reminder and reports whether one was found.</summary>
    Task<bool> UnregisterReminderAsync(string reminderName);

    /// <summary>Returns the names of the reminders currently registered for this grain.</summary>
    Task<List<string>> GetReminderNamesAsync();
}

/// <inheritdoc cref="IReminderTestKitGrain" />
public sealed class ReminderTestKitGrain : Grain, IReminderTestKitGrain, IRemindable
{
    /// <inheritdoc />
    public async Task<string> RegisterReminderAsync(string reminderName, TimeSpan dueTime, TimeSpan period)
    {
        var reminder = await this.RegisterOrUpdateReminder(reminderName, dueTime, period);
        return reminder.ReminderName;
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterReminderAsync(string reminderName)
    {
        var reminder = await this.GetReminder(reminderName);
        if (reminder is null)
        {
            return false;
        }

        await this.UnregisterReminder(reminder);
        return true;
    }

    /// <inheritdoc />
    public async Task<List<string>> GetReminderNamesAsync()
    {
        var reminders = await this.GetReminders();
        return [.. reminders.Select(reminder => reminder.ReminderName)];
    }

    /// <inheritdoc />
    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}
