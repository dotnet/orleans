using System;
using System.Collections.Generic;
using System.Threading;

#nullable enable
namespace Orleans;

/// <summary>
/// Streams reminders page-by-page from <see cref="IReminderManagementGrain"/> without materializing all pages at once.
/// </summary>
public interface IReminderIterator
{
    /// <summary>
    /// Enumerates all reminders.
    /// </summary>
    IAsyncEnumerable<ReminderEntry> EnumerateAllAsync(int pageSize = 256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates reminders overdue by at least <paramref name="overdueBy"/>.
    /// </summary>
    IAsyncEnumerable<ReminderEntry> EnumerateOverdueAsync(TimeSpan overdueBy, int pageSize = 256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates reminders due in the provided UTC range.
    /// </summary>
    IAsyncEnumerable<ReminderEntry> EnumerateDueInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        int pageSize = 256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates reminders matching the provided filter.
    /// </summary>
    IAsyncEnumerable<ReminderEntry> EnumerateFilteredAsync(
        ReminderQueryFilter filter,
        int pageSize = 256,
        CancellationToken cancellationToken = default);
}
