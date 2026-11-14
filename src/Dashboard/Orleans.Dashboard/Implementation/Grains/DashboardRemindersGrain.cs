using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Model;
using ClassicReminderEntry = Orleans.ReminderEntry;
using ClassicReminderTable = Orleans.IReminderTable;

namespace Orleans.Dashboard.Implementation.Grains;

internal sealed class DashboardRemindersGrain : Grain, IDashboardRemindersGrain
{
    private const int MaxPageTokenReplayCount = 32;
    private const int MaxCachedAdvancedPageTokens = 64;
    private static readonly TimeSpan AdvancedReminderCountCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly Immutable<ReminderResponse> EmptyReminders = new ReminderResponse
    {
        Reminders = []
    }.AsImmutable();

    private static readonly Immutable<AdvancedReminderResponse> EmptyAdvancedReminders = new AdvancedReminderResponse
    {
        Reminders = []
    }.AsImmutable();

    private readonly IAdvancedReminderDashboardSource? _advancedReminders;
    private readonly ClassicReminderTable? _classicReminderTable;
    private readonly Dictionary<(int PageSize, int PageNumber), string?> _advancedPageTokens = new();
    private readonly Queue<(int PageSize, int PageNumber)> _advancedPageTokenOrder = new();
    private readonly TimeProvider _timeProvider;
    private long? _advancedReminderCount;
    private long _advancedReminderCountTimestamp;

    public DashboardRemindersGrain(IServiceProvider serviceProvider)
    {
        _advancedReminders = serviceProvider.GetService<IAdvancedReminderDashboardSource>();
        _classicReminderTable = serviceProvider.GetService<ClassicReminderTable>();
        _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    public async Task<Immutable<ReminderResponse>> GetReminders(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_classicReminderTable == null)
        {
            return EmptyReminders;
        }

        ValidatePagingArguments(pageNumber, pageSize);

        var reminderData = await _classicReminderTable.ReadRows(0, 0, cancellationToken);

        if (!reminderData.Reminders.Any())
        {
            return EmptyReminders;
        }

        var skip = GetSkipCount(pageNumber, pageSize, reminderData.Reminders.Count);
        return new ReminderResponse
        {
            Reminders = skip is null
                ? []
                : reminderData
                .Reminders
                .OrderBy(x => x.StartAt)
                .Skip(skip.Value)
                .Take(pageSize)
                .Select(ToReminderInfo)
                .ToArray(),

            Count = reminderData.Reminders.Count
        }.AsImmutable();
    }

    public async Task<Immutable<AdvancedReminderResponse>> GetAdvancedReminders(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_advancedReminders is null)
        {
            return EmptyAdvancedReminders;
        }

        ValidatePagingArguments(pageNumber, pageSize);

        var count = await GetAdvancedReminderCount(cancellationToken);
        var token = await GetAdvancedPageToken(pageNumber, pageSize, cancellationToken);
        if (pageNumber > 1 && token is null)
        {
            return new AdvancedReminderResponse
            {
                Reminders = [],
                Count = count,
                HasMore = false,
            }.AsImmutable();
        }

        var page = await _advancedReminders.ReadPageAsync(pageSize, token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CacheAdvancedPageToken(pageSize, pageNumber + 1, page.ContinuationToken);

        return new AdvancedReminderResponse
        {
            Reminders = page.Reminders,
            Count = count,
            HasMore = page.ContinuationToken is not null,
        }.AsImmutable();
    }

    private async Task<long> GetAdvancedReminderCount(CancellationToken cancellationToken)
    {
        if (_advancedReminderCount is { } cached
            && _timeProvider.GetElapsedTime(_advancedReminderCountTimestamp) < AdvancedReminderCountCacheDuration)
        {
            return cached;
        }

        var count = await _advancedReminders!.GetCountAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _advancedReminderCount = count;
        _advancedReminderCountTimestamp = _timeProvider.GetTimestamp();
        return count;
    }

    private async Task<string?> GetAdvancedPageToken(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (pageNumber == 1)
        {
            return null;
        }

        if (_advancedPageTokens.TryGetValue((pageSize, pageNumber), out var cached))
        {
            return cached;
        }

        if (pageNumber - 1 > MaxPageTokenReplayCount)
        {
            return null;
        }

        var currentPage = 1;
        string? token = null;
        while (currentPage < pageNumber)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _advancedReminders!.ReadPageAsync(pageSize, token, cancellationToken);
            token = page.ContinuationToken;
            currentPage++;
            CacheAdvancedPageToken(pageSize, currentPage, token);
            if (token is null)
            {
                break;
            }
        }

        return token;
    }

    private void CacheAdvancedPageToken(int pageSize, int pageNumber, string? token)
    {
        var key = (pageSize, pageNumber);
        if (!_advancedPageTokens.TryAdd(key, token))
        {
            _advancedPageTokens[key] = token;
            return;
        }

        _advancedPageTokenOrder.Enqueue(key);
        if (_advancedPageTokens.Count > MaxCachedAdvancedPageTokens)
        {
            _advancedPageTokens.Remove(_advancedPageTokenOrder.Dequeue());
        }
    }

    private static void ValidatePagingArguments(int pageNumber, int pageSize)
    {
        if (pageNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
    }

    private static int? GetSkipCount(int pageNumber, int pageSize, int count)
    {
        var skip = ((long)pageNumber - 1) * pageSize;
        return skip >= count ? null : (int)skip;
    }

    private static ReminderInfo ToReminderInfo(ClassicReminderEntry entry)
    {
        return new ReminderInfo
        {
            PrimaryKey = entry.GrainId.Key.ToString(),
            GrainReference = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
        };
    }
}
