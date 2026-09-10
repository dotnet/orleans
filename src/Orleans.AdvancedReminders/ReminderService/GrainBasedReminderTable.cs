using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

[Reentrant]
[KeepAlive]
internal sealed class AdvancedReminderTableGrain : Grain, IReminderTableGrain, IGrainMigrationParticipant
{
    private Dictionary<GrainId, Dictionary<string, ReminderEntry>> _reminderTable = new();
    private long _reminderCount;

    public Task<long> GetCount()
        => Task.FromResult(_reminderCount);

    public Task<ReminderTableData> ReadRows(GrainId grainId)
        => Task.FromResult(
            _reminderTable.TryGetValue(grainId, out var reminders)
                ? new ReminderTableData(reminders.Values)
                : new ReminderTableData());

    public Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        var range = RangeFactory.CreateRange(begin, end);
        var result = new List<ReminderEntry>();
        foreach (var (grainId, reminders) in _reminderTable)
        {
            if (range.InRange(grainId))
            {
                result.AddRange(reminders.Values);
            }

        }

        return Task.FromResult(new ReminderTableData(result));
    }

    public Task<ReminderTableData> ReadRows(uint begin, uint end, int maxRows, string? continuationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        var range = RangeFactory.CreateRange(begin, end);
        var cursor = ParseContinuationToken(continuationToken);
        // Keep the latest row at the head so a better candidate can replace it.
        var candidates = new PriorityQueue<ReminderEntry, ReminderOrder>(ReminderOrder.LatestFirst);
        var hasMore = false;
        foreach (var (grainId, reminders) in _reminderTable)
        {
            var hash = grainId.GetUniformHashCode();
            if (!range.InRange(hash))
            {
                continue;
            }

            var distance = unchecked(hash - begin);
            var comparison = cursor is { } after ? CompareToCursor(grainId, distance, after, begin) : 1;
            if (comparison < 0)
            {
                continue;
            }

            if (comparison > 0 && candidates.Count == maxRows
                && candidates.TryPeek(out _, out var latest) && distance > latest.Distance)
            {
                hasMore = true;
                continue;
            }

            var afterName = comparison == 0 ? cursor.GetValueOrDefault().ReminderName : null;
            foreach (var entry in reminders.Values)
            {
                if (afterName is not null && string.CompareOrdinal(entry.ReminderName, afterName) <= 0)
                {
                    continue;
                }

                var priority = new ReminderOrder(distance, entry);
                if (candidates.Count < maxRows)
                {
                    candidates.Enqueue(entry, priority);
                }
                else
                {
                    hasMore = true;
                    candidates.EnqueueDequeue(entry, priority);
                }
            }
        }

        var rows = new List<ReminderEntry>(candidates.Count);
        while (candidates.TryDequeue(out var entry, out _))
        {
            rows.Add(entry);
        }
        rows.Reverse();

        return Task.FromResult(new ReminderTableData(
            rows,
            hasMore ? FormatContinuationToken(rows[^1]) : null));
    }

    private static int CompareToCursor(GrainId grainId, uint distance, ReminderCursor cursor, uint begin)
    {
        var hashComparison = distance.CompareTo(unchecked(cursor.Hash - begin));
        if (hashComparison != 0)
        {
            return hashComparison;
        }

        return string.CompareOrdinal(grainId.ToString(), cursor.GrainId);
    }

    private static string FormatContinuationToken(ReminderEntry entry)
        => string.Concat(
            entry.GrainId.GetUniformHashCode().ToString("X8", CultureInfo.InvariantCulture),
            ".",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.GrainId.ToString())),
            ".",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.ReminderName)));

    private static ReminderCursor? ParseContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return null;
        }

        try
        {
            var segments = continuationToken.Split('.');
            if (segments.Length != 3
                || !uint.TryParse(segments[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
            {
                throw new FormatException();
            }

            return new ReminderCursor(
                hash,
                Encoding.UTF8.GetString(Convert.FromBase64String(segments[1])),
                Encoding.UTF8.GetString(Convert.FromBase64String(segments[2])));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(continuationToken), exception);
        }
    }

    private readonly record struct ReminderCursor(uint Hash, string GrainId, string ReminderName);

    private readonly record struct ReminderOrder(uint Distance, ReminderEntry Entry)
    {
        public static readonly Comparer<ReminderOrder> LatestFirst = Comparer<ReminderOrder>.Create(static (left, right) =>
        {
            var comparison = right.Distance.CompareTo(left.Distance);
            if (comparison == 0 && left.Entry.GrainId != right.Entry.GrainId)
            {
                comparison = string.CompareOrdinal(right.Entry.GrainId.ToString(), left.Entry.GrainId.ToString());
            }

            return comparison != 0 ? comparison : string.CompareOrdinal(right.Entry.ReminderName, left.Entry.ReminderName);
        });
    }

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        ReminderEntry? result = null;
        if (_reminderTable.TryGetValue(grainId, out var reminders))
        {
            reminders.TryGetValue(reminderName, out result);
        }

        return Task.FromResult(result);
    }

    public Task<string> UpsertRow(ReminderEntry entry)
    {
        if (_reminderTable.TryGetValue(entry.GrainId, out var existingReminders)
            && existingReminders.TryGetValue(entry.ReminderName, out var existing))
        {
            if (string.IsNullOrEmpty(entry.ETag)
                || !string.Equals(existing.ETag, entry.ETag, StringComparison.Ordinal))
            {
                throw new Runtime.ReminderException(
                    $"Could not update reminder '{entry.ReminderName}' for grain '{entry.GrainId}' due to ETag mismatch.");
            }
        }
        else if (!string.IsNullOrEmpty(entry.ETag))
        {
            throw new Runtime.ReminderException(
                $"Could not update missing reminder '{entry.ReminderName}' for grain '{entry.GrainId}'.");
        }

        entry.ETag = Guid.NewGuid().ToString("N");
        var reminders = CollectionsMarshal.GetValueRefOrAddDefault(_reminderTable, entry.GrainId, out _) ??= new(StringComparer.Ordinal);
        CollectionsMarshal.GetValueRefOrAddDefault(reminders, entry.ReminderName, out var exists) = entry;
        if (!exists)
        {
            _reminderCount++;
        }

        return Task.FromResult(entry.ETag);
    }

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        if (!_reminderTable.TryGetValue(grainId, out var reminders)
            || !reminders.TryGetValue(reminderName, out var entry)
            || !string.Equals(entry.ETag, eTag, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        reminders.Remove(reminderName);
        _reminderCount--;
        if (reminders.Count == 0)
        {
            _reminderTable.Remove(grainId);
        }

        return Task.FromResult(true);
    }

    public Task TestOnlyClearTable()
    {
        _reminderTable.Clear();
        _reminderCount = 0;
        return Task.CompletedTask;
    }

    void IGrainMigrationParticipant.OnDehydrate(IDehydrationContext dehydrationContext)
        => dehydrationContext.TryAddValue("table", _reminderTable);

    void IGrainMigrationParticipant.OnRehydrate(IRehydrationContext rehydrationContext)
    {
        if (rehydrationContext.TryGetValue("table", out Dictionary<GrainId, Dictionary<string, ReminderEntry>>? table)
            && table is not null)
        {
            _reminderTable = table;
            _reminderCount = table.Values.Sum(static reminders => (long)reminders.Count);
        }
    }
}
