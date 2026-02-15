using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

#nullable enable
namespace Orleans;

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
    public static async IAsyncEnumerable<ReminderEntry> EnumerateAllAsync(
        this IReminderManagementGrain managementGrain,
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);

        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await managementGrain.ListAllAsync(pageSize, continuationToken).WaitAsync(cancellationToken);

            foreach (var reminder in page.Reminders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return reminder;
            }

            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    /// <summary>
    /// Iterates overdue reminders across the reminder table using server-side paging.
    /// </summary>
    public static async IAsyncEnumerable<ReminderEntry> EnumerateOverdueAsync(
        this IReminderManagementGrain managementGrain,
        TimeSpan overdueBy,
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);

        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await managementGrain.ListOverdueAsync(overdueBy, pageSize, continuationToken).WaitAsync(cancellationToken);

            foreach (var reminder in page.Reminders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return reminder;
            }

            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    /// <summary>
    /// Iterates reminders due in the provided UTC range using server-side paging.
    /// </summary>
    public static async IAsyncEnumerable<ReminderEntry> EnumerateDueInRangeAsync(
        this IReminderManagementGrain managementGrain,
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);

        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await managementGrain
                .ListDueInRangeAsync(fromUtcInclusive, toUtcInclusive, pageSize, continuationToken)
                .WaitAsync(cancellationToken);

            foreach (var reminder in page.Reminders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return reminder;
            }

            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    /// <summary>
    /// Iterates reminders matching the provided server-side filter using paging.
    /// </summary>
    public static async IAsyncEnumerable<ReminderEntry> EnumerateFilteredAsync(
        this IReminderManagementGrain managementGrain,
        ReminderQueryFilter filter,
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(managementGrain);
        ArgumentNullException.ThrowIfNull(filter);

        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await managementGrain
                .ListFilteredAsync(filter, pageSize, continuationToken)
                .WaitAsync(cancellationToken);

            foreach (var reminder in page.Reminders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return reminder;
            }

            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }
}
