using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

internal readonly record struct ReminderTableEntryIdentity(GrainId GrainId, string ReminderName)
{
    public override string ToString() => $"({GrainId}, '{ReminderName}')";
}

internal readonly record struct ReminderTableEntrySnapshot(
    GrainId GrainId,
    string ReminderName,
    long StartAtTicks,
    long PeriodTicks,
    string? ETag)
{
    public ReminderTableEntryIdentity Identity => new(GrainId, ReminderName);

    public static ReminderTableEntrySnapshot Create(
        ReminderEntry entry,
        string? etag,
        bool supportsSubSecondPrecision)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new(
            entry.GrainId,
            entry.ReminderName,
            NormalizeStartAtTicks(entry.StartAt, supportsSubSecondPrecision),
            entry.Period.Ticks,
            etag);
    }

    public static ReminderTableEntrySnapshot Observe(
        ReminderEntry entry,
        bool supportsSubSecondPrecision)
        => Create(entry, entry.ETag, supportsSubSecondPrecision);

    public static long NormalizeStartAtTicks(DateTime value, bool supportsSubSecondPrecision)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return supportsSubSecondPrecision
            ? utc.Ticks
            : utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond);
    }

    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{{ GrainId={GrainId}, ReminderName='{ReminderName}', StartAtTicks={StartAtTicks}, PeriodTicks={PeriodTicks}, ETag={FormatETag(ETag)} }}");

    private static string FormatETag(string? etag) => etag is null ? "<null>" : $"'{etag}'";
}

internal sealed class ReminderTableEntryComparisonFailure
{
    public ReminderTableEntryComparisonFailure(
        string field,
        string message,
        ReminderTableEntrySnapshot? expected = null,
        ReminderTableEntrySnapshot? actual = null)
    {
        Field = field;
        Message = message;
        Expected = expected;
        Actual = actual;
    }

    public string Field { get; }

    public string Message { get; }

    public ReminderTableEntrySnapshot? Expected { get; }

    public ReminderTableEntrySnapshot? Actual { get; }

    public override string ToString()
        => $"{Message} DifferingField={Field}; Expected={Format(Expected)}; Actual={Format(Actual)}.";

    private static string Format(ReminderTableEntrySnapshot? snapshot)
        => snapshot?.ToString() ?? "<none>";
}

internal static class ReminderTableEntrySnapshotComparer
{
    public static ReminderTableEntryComparisonFailure? Compare(
        ReminderTableEntrySnapshot expected,
        ReminderTableEntrySnapshot actual)
    {
        if (!expected.GrainId.Equals(actual.GrainId))
        {
            return Different(nameof(ReminderTableEntrySnapshot.GrainId), expected, actual);
        }

        if (!string.Equals(expected.ReminderName, actual.ReminderName, StringComparison.Ordinal))
        {
            return Different(nameof(ReminderTableEntrySnapshot.ReminderName), expected, actual);
        }

        if (expected.StartAtTicks != actual.StartAtTicks)
        {
            return Different(nameof(ReminderTableEntrySnapshot.StartAtTicks), expected, actual);
        }

        if (expected.PeriodTicks != actual.PeriodTicks)
        {
            return Different(nameof(ReminderTableEntrySnapshot.PeriodTicks), expected, actual);
        }

        if (!string.Equals(expected.ETag, actual.ETag, StringComparison.Ordinal))
        {
            return Different(nameof(ReminderTableEntrySnapshot.ETag), expected, actual);
        }

        return null;
    }

    public static ReminderTableEntryComparisonFailure? CompareExact(
        IReadOnlyList<ReminderTableEntrySnapshot> expected,
        IReadOnlyList<ReminderTableEntrySnapshot> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var duplicate = FindDuplicate(expected);
        if (duplicate is { } duplicateExpected)
        {
            return new(
                "ExpectedIdentityMultiplicity",
                $"Expected entries contain duplicate identity {duplicateExpected.Identity}.",
                duplicateExpected);
        }

        duplicate = FindDuplicate(actual);
        if (duplicate is { } duplicateActual)
        {
            return new(
                "ActualIdentityMultiplicity",
                $"Observed entries contain duplicate identity {duplicateActual.Identity}.",
                actual: duplicateActual);
        }

        if (expected.Count != actual.Count)
        {
            return new(
                "Count",
                $"Entry count differs: expected {expected.Count.ToString(CultureInfo.InvariantCulture)}, observed {actual.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        var expectedByIdentity = expected.ToDictionary(snapshot => snapshot.Identity);
        foreach (var observation in actual)
        {
            if (!expectedByIdentity.TryGetValue(observation.Identity, out var expectedSnapshot))
            {
                return new(
                    nameof(ReminderTableEntrySnapshot.Identity),
                    $"Observed unknown identity {observation.Identity}.",
                    actual: observation);
            }

            var difference = Compare(expectedSnapshot, observation);
            if (difference is not null)
            {
                return difference;
            }

            expectedByIdentity.Remove(observation.Identity);
        }

        if (expectedByIdentity.Count > 0)
        {
            var missing = expectedByIdentity.Values.First();
            return new(
                nameof(ReminderTableEntrySnapshot.Identity),
                $"Expected identity {missing.Identity} was not observed.",
                missing);
        }

        return null;
    }

    private static ReminderTableEntrySnapshot? FindDuplicate(IReadOnlyList<ReminderTableEntrySnapshot> snapshots)
    {
        for (var index = 0; index < snapshots.Count; index++)
        {
            for (var candidate = index + 1; candidate < snapshots.Count; candidate++)
            {
                if (snapshots[index].Identity == snapshots[candidate].Identity)
                {
                    return snapshots[candidate];
                }
            }
        }

        return null;
    }

    private static ReminderTableEntryComparisonFailure Different(
        string field,
        ReminderTableEntrySnapshot expected,
        ReminderTableEntrySnapshot actual)
        => new(field, $"Reminder entry field '{field}' differs.", expected, actual);
}
