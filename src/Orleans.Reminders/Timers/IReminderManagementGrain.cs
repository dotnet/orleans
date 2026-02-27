using Orleans.Runtime;

#nullable enable
namespace Orleans;

/// <summary>
/// Administrative management API for adaptive reminders.
/// </summary>
public interface IReminderManagementGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Returns a page of reminders across the whole reminder table.
    /// </summary>
    /// <param name="pageSize">The maximum number of reminders to return.</param>
    /// <param name="continuationToken">An opaque continuation token from a previous page, or <see langword="null"/> to start from the beginning.</param>
    Task<ReminderManagementPage> ListAllAsync(int pageSize = 256, string? continuationToken = null);

    /// <summary>
    /// Returns a page of overdue reminders across the whole reminder table.
    /// A reminder is considered overdue when <c>NextDueUtc</c> (or <c>StartAt</c> if missing) is less than or equal to <c>UtcNow - overdueBy</c>.
    /// </summary>
    /// <param name="overdueBy">The minimum overdue duration to match.</param>
    /// <param name="pageSize">The maximum number of reminders to return.</param>
    /// <param name="continuationToken">An opaque continuation token from a previous page, or <see langword="null"/> to start from the beginning.</param>
    Task<ReminderManagementPage> ListOverdueAsync(TimeSpan overdueBy, int pageSize = 256, string? continuationToken = null);

    /// <summary>
    /// Returns a page of reminders due within the provided UTC range.
    /// A reminder due value is <c>NextDueUtc</c> when present, otherwise <c>StartAt</c>.
    /// </summary>
    /// <param name="fromUtcInclusive">Range start (inclusive), must be UTC.</param>
    /// <param name="toUtcInclusive">Range end (inclusive), must be UTC.</param>
    /// <param name="pageSize">The maximum number of reminders to return.</param>
    /// <param name="continuationToken">An opaque continuation token from a previous page, or <see langword="null"/> to start from the beginning.</param>
    Task<ReminderManagementPage> ListDueInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        int pageSize = 256,
        string? continuationToken = null);

    /// <summary>
    /// Returns a page of reminders matching the provided server-side filter.
    /// </summary>
    /// <param name="filter">Filter criteria. Date values must be UTC when provided.</param>
    /// <param name="pageSize">The maximum number of reminders to return.</param>
    /// <param name="continuationToken">An opaque continuation token from a previous page, or <see langword="null"/> to start from the beginning.</param>
    Task<ReminderManagementPage> ListFilteredAsync(
        ReminderQueryFilter filter,
        int pageSize = 256,
        string? continuationToken = null);

    /// <summary>
    /// Returns reminders due within the specified horizon.
    /// </summary>
    Task<IEnumerable<ReminderEntry>> UpcomingAsync(TimeSpan horizon);

    /// <summary>
    /// Returns all reminders for the specified grain.
    /// </summary>
    Task<IEnumerable<ReminderEntry>> ListForGrainAsync(GrainId grainId);

    /// <summary>
    /// Returns the total reminder count.
    /// </summary>
    Task<int> CountAllAsync();

    /// <summary>
    /// Sets reminder priority.
    /// </summary>
    Task SetPriorityAsync(GrainId grainId, string name, Runtime.ReminderPriority priority);

    /// <summary>
    /// Sets missed reminder action.
    /// </summary>
    Task SetActionAsync(GrainId grainId, string name, Runtime.MissedReminderAction action);

    /// <summary>
    /// Repairs a reminder by recalculating its next due time.
    /// </summary>
    Task RepairAsync(GrainId grainId, string name);

    /// <summary>
    /// Deletes the specified reminder.
    /// </summary>
    Task DeleteAsync(GrainId grainId, string name);
}
