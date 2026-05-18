using System;
using System.Collections.Generic;
using System.Threading;

#nullable enable
namespace Orleans.AdvancedReminders;

/// <summary>
/// Helper methods for iterating reminder management queries page-by-page.
/// </summary>
public static class ReminderManagementGrainExtensions
{
    /// <summary>
    /// Creates an iterator facade for reminder management paging APIs.
    /// </summary>
    public static IReminderIterator CreateIterator(this IReminderManagementGrain managementGrain)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);
        return new ReminderIterator(managementGrain);
    }

    /// <summary>
    /// Iterates all reminders across the reminder table using server-side paging.
    /// </summary>
    public static IAsyncEnumerable<ReminderEntry> EnumerateAllAsync(
        this IReminderManagementGrain managementGrain,
        int pageSize = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);
        return managementGrain.CreateIterator().EnumerateAllAsync(pageSize, cancellationToken);
    }

    /// <summary>
    /// Iterates overdue reminders across the reminder table using server-side paging.
    /// </summary>
    public static IAsyncEnumerable<ReminderEntry> EnumerateOverdueAsync(
        this IReminderManagementGrain managementGrain,
        TimeSpan overdueBy,
        int pageSize = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);
        return managementGrain.CreateIterator().EnumerateOverdueAsync(overdueBy, pageSize, cancellationToken);
    }

    /// <summary>
    /// Iterates reminders due in the provided UTC range using server-side paging.
    /// </summary>
    public static IAsyncEnumerable<ReminderEntry> EnumerateDueInRangeAsync(
        this IReminderManagementGrain managementGrain,
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        int pageSize = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);
        return managementGrain.CreateIterator().EnumerateDueInRangeAsync(fromUtcInclusive, toUtcInclusive, pageSize, cancellationToken);
    }

    /// <summary>
    /// Iterates reminders matching the provided server-side filter using paging.
    /// </summary>
    public static IAsyncEnumerable<ReminderEntry> EnumerateFilteredAsync(
        this IReminderManagementGrain managementGrain,
        ReminderQueryFilter filter,
        int pageSize = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);
        ArgumentNullException.ThrowIfNull(filter);
        return managementGrain.CreateIterator().EnumerateFilteredAsync(filter, pageSize, cancellationToken);
    }
}
