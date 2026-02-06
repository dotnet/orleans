using System;
using System.Collections.Generic;

#nullable enable
namespace Orleans;

/// <summary>
/// Represents a page of reminders returned from <see cref="IReminderManagementGrain"/>.
/// </summary>
[GenerateSerializer]
public sealed class ReminderManagementPage
{
    /// <summary>
    /// Gets the reminders in this page.
    /// </summary>
    [Id(0)]
    public List<ReminderEntry> Reminders { get; init; } = [];

    /// <summary>
    /// Gets the opaque continuation token for fetching the next page, or <see langword="null"/> when there are no more pages.
    /// </summary>
    [Id(1)]
    public string? ContinuationToken { get; init; }
}
