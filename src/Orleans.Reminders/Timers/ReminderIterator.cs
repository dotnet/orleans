using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

#nullable enable
namespace Orleans;

/// <summary>
/// Default implementation of <see cref="IReminderIterator"/> backed by <see cref="IReminderManagementGrain"/> paging APIs.
/// </summary>
public sealed class ReminderIterator(IReminderManagementGrain managementGrain) : IReminderIterator
{
    private readonly IReminderManagementGrain _managementGrain = managementGrain ?? throw new ArgumentNullException(nameof(managementGrain));

    public async IAsyncEnumerable<ReminderEntry> EnumerateAllAsync(
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _managementGrain.ListAllAsync(pageSize, continuationToken).WaitAsync(cancellationToken);

            foreach (var reminder in page.Reminders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return reminder;
            }

            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    public async IAsyncEnumerable<ReminderEntry> EnumerateOverdueAsync(
        TimeSpan overdueBy,
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _managementGrain.ListOverdueAsync(overdueBy, pageSize, continuationToken).WaitAsync(cancellationToken);

            foreach (var reminder in page.Reminders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return reminder;
            }

            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    public async IAsyncEnumerable<ReminderEntry> EnumerateDueInRangeAsync(
        DateTime fromUtcInclusive,
        DateTime toUtcInclusive,
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _managementGrain
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

    public async IAsyncEnumerable<ReminderEntry> EnumerateFilteredAsync(
        ReminderQueryFilter filter,
        int pageSize = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _managementGrain
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
