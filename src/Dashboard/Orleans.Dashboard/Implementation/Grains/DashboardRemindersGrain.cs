using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.AdvancedReminders;
using Orleans.Concurrency;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Model;
using AdvancedReminderEntry = Orleans.AdvancedReminders.ReminderEntry;
using AdvancedReminderTable = Orleans.AdvancedReminders.IReminderTable;
using ClassicReminderEntry = Orleans.ReminderEntry;
using ClassicReminderTable = Orleans.IReminderTable;

#nullable disable
namespace Orleans.Dashboard.Implementation.Grains;

internal sealed class DashboardRemindersGrain : Grain, IDashboardRemindersGrain
{
    private const int MaxPageTokenReplayCount = 32;
    private const int AdvancedReminderScanBucketShift = 24;
    private static readonly Immutable<ReminderResponse> EmptyReminders = new ReminderResponse
    {
        Reminders = []
    }.AsImmutable();

    private static readonly Immutable<AdvancedReminderResponse> EmptyAdvancedReminders = new AdvancedReminderResponse
    {
        Reminders = []
    }.AsImmutable();

    private readonly AdvancedReminderTable _advancedReminderTable;
    private readonly IReminderManagementGrain _advancedReminderManagement;
    private readonly ClassicReminderTable _classicReminderTable;
    private readonly Dictionary<(int PageSize, int PageNumber), string> _advancedPageTokens = new();

    public DashboardRemindersGrain(IServiceProvider serviceProvider, IGrainFactory grainFactory)
        : this(serviceProvider, grainFactory.GetReminderManagementGrain())
    {
    }

    internal DashboardRemindersGrain(IServiceProvider serviceProvider)
        : this(serviceProvider, advancedReminderManagement: null)
    {
    }

    internal DashboardRemindersGrain(
        IServiceProvider serviceProvider,
        IReminderManagementGrain advancedReminderManagement)
    {
        _advancedReminderTable = serviceProvider.GetService(typeof(AdvancedReminderTable)) as AdvancedReminderTable;
        _classicReminderTable = serviceProvider.GetService(typeof(ClassicReminderTable)) as ClassicReminderTable;
        _advancedReminderManagement = advancedReminderManagement;
    }

    public async Task<Immutable<ReminderResponse>> GetReminders(int pageNumber, int pageSize)
    {
        if (_classicReminderTable == null)
        {
            return EmptyReminders;
        }

        ValidatePagingArguments(pageNumber, pageSize);

        var reminderData = await _classicReminderTable.ReadRows(0, 0);

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

    public async Task<Immutable<AdvancedReminderResponse>> GetAdvancedReminders(int pageNumber, int pageSize)
    {
        if (_advancedReminderTable == null)
        {
            return EmptyAdvancedReminders;
        }

        ValidatePagingArguments(pageNumber, pageSize);

        if (_advancedReminderManagement is null)
        {
            return await GetAdvancedRemindersFromTable(pageNumber, pageSize);
        }

        var token = await GetAdvancedPageToken(pageNumber, pageSize);
        if (pageNumber > 1 && token is null)
        {
            return new AdvancedReminderResponse
            {
                Reminders = [],
                Count = 0,
                HasMore = false,
            }.AsImmutable();
        }

        var page = await _advancedReminderManagement.ListAllAsync(pageSize, token);
        _advancedPageTokens[(pageSize, pageNumber + 1)] = page.ContinuationToken;

        return new AdvancedReminderResponse
        {
            Reminders = page.Reminders.Select(ToAdvancedReminderInfo).ToArray(),
            Count = 0,
            HasMore = page.ContinuationToken is not null,
        }.AsImmutable();
    }

    private async Task<Immutable<AdvancedReminderResponse>> GetAdvancedRemindersFromTable(int pageNumber, int pageSize)
    {
        var reminderData = await _advancedReminderTable.ReadRows(0, 0);

        if (!reminderData.Reminders.Any())
        {
            return EmptyAdvancedReminders;
        }

        var skip = GetSkipCount(pageNumber, pageSize, reminderData.Reminders.Count);
        return new AdvancedReminderResponse
        {
            Reminders = skip is null
                ? []
                : reminderData
                .Reminders
                .OrderBy(GetAdvancedReminderScanBucket)
                .ThenBy(x => x.NextDueUtc ?? x.StartAt)
                .ThenBy(x => x.GrainId)
                .ThenBy(x => x.ReminderName, StringComparer.Ordinal)
                .Skip(skip.Value)
                .Take(pageSize)
                .Select(ToAdvancedReminderInfo)
                .ToArray(),

            Count = reminderData.Reminders.Count,
            HasMore = skip is not null && skip.Value + pageSize < reminderData.Reminders.Count,
        }.AsImmutable();
    }

    private static int GetAdvancedReminderScanBucket(AdvancedReminderEntry entry)
        => (int)(entry.GrainId.GetUniformHashCode() >> AdvancedReminderScanBucketShift);

    private async Task<string> GetAdvancedPageToken(int pageNumber, int pageSize)
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
        string token = null;
        while (currentPage < pageNumber)
        {
            var page = await _advancedReminderManagement.ListAllAsync(pageSize, token);
            token = page.ContinuationToken;
            currentPage++;
            _advancedPageTokens[(pageSize, currentPage)] = token;
            if (token is null)
            {
                break;
            }
        }

        return token;
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

    private static AdvancedReminderInfo ToAdvancedReminderInfo(AdvancedReminderEntry entry)
    {
        return new AdvancedReminderInfo
        {
            PrimaryKey = entry.GrainId.Key.ToString(),
            GrainReference = entry.GrainId.ToString(),
            Name = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            CronExpression = entry.CronExpression,
            CronTimeZoneId = entry.CronTimeZoneId,
            NextDueUtc = entry.NextDueUtc,
            LastFireUtc = entry.LastFireUtc,
            Priority = entry.Priority.ToString(),
            MissedAction = entry.Action.ToString(),
        };
    }
}
