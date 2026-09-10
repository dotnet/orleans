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
        var query = _reminderTable
            .Where(pair => range.InRange(pair.Key))
            .SelectMany(pair => pair.Value.Values)
            .OrderBy(entry => unchecked(entry.GrainId.GetUniformHashCode() - begin))
            .ThenBy(entry => entry.GrainId.ToString(), StringComparer.Ordinal)
            .ThenBy(entry => entry.ReminderName, StringComparer.Ordinal);
        if (cursor is not null)
        {
            query = query.Where(entry => CompareToCursor(entry, cursor.Value, begin) > 0)
                .OrderBy(entry => unchecked(entry.GrainId.GetUniformHashCode() - begin))
                .ThenBy(entry => entry.GrainId.ToString(), StringComparer.Ordinal)
                .ThenBy(entry => entry.ReminderName, StringComparer.Ordinal);
        }

        var rows = query
            .Take(maxRows + 1)
            .ToList();
        var hasMore = rows.Count > maxRows;
        if (hasMore)
        {
            rows.RemoveAt(maxRows);
        }

        return Task.FromResult(new ReminderTableData(
            rows,
            hasMore ? FormatContinuationToken(rows[^1]) : null));
    }

    private static int CompareToCursor(ReminderEntry entry, ReminderCursor cursor, uint begin)
    {
        var hashComparison = unchecked(entry.GrainId.GetUniformHashCode() - begin)
            .CompareTo(unchecked(cursor.Hash - begin));
        if (hashComparison != 0)
        {
            return hashComparison;
        }

        var grainComparison = string.CompareOrdinal(entry.GrainId.ToString(), cursor.GrainId);
        return grainComparison != 0
            ? grainComparison
            : string.CompareOrdinal(entry.ReminderName, cursor.ReminderName);
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
        CollectionsMarshal.GetValueRefOrAddDefault(reminders, entry.ReminderName, out _) = entry;
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
        if (reminders.Count == 0)
        {
            _reminderTable.Remove(grainId);
        }

        return Task.FromResult(true);
    }

    public Task TestOnlyClearTable()
    {
        _reminderTable.Clear();
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
        }
    }
}
