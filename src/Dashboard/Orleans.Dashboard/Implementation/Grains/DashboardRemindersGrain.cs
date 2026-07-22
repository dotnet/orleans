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
    private DateTime _advancedCacheExpiresUtc;
    private int? _advancedCount;

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

        var reminderData = await _classicReminderTable.ReadRows(0, 0);

        if (!reminderData.Reminders.Any())
        {
            return EmptyReminders;
        }

        return new ReminderResponse
        {
            Reminders = reminderData
                .Reminders
                .OrderBy(x => x.StartAt)
                .Skip((pageNumber - 1) * pageSize)
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

        if (pageNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (_advancedReminderManagement is null)
        {
            return await GetAdvancedRemindersFromTable(pageNumber, pageSize);
        }

        RefreshAdvancedCacheIfExpired();
        var token = await GetAdvancedPageToken(pageNumber, pageSize);
        if (pageNumber > 1 && token is null)
        {
            return new AdvancedReminderResponse
            {
                Reminders = [],
                Count = await GetAdvancedCount(),
            }.AsImmutable();
        }

        var page = await _advancedReminderManagement.ListAllAsync(pageSize, token);
        _advancedPageTokens[(pageSize, pageNumber + 1)] = page.ContinuationToken;

        return new AdvancedReminderResponse
        {
            Reminders = page.Reminders.Select(ToAdvancedReminderInfo).ToArray(),
            Count = await GetAdvancedCount(),
        }.AsImmutable();
    }

    private async Task<Immutable<AdvancedReminderResponse>> GetAdvancedRemindersFromTable(int pageNumber, int pageSize)
    {
        var reminderData = await _advancedReminderTable.ReadRows(0, 0);

        if (!reminderData.Reminders.Any())
        {
            return EmptyAdvancedReminders;
        }

        return new AdvancedReminderResponse
        {
            Reminders = reminderData
                .Reminders
                .OrderBy(x => x.NextDueUtc ?? x.StartAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(ToAdvancedReminderInfo)
                .ToArray(),

            Count = reminderData.Reminders.Count
        }.AsImmutable();
    }

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

    private async Task<int> GetAdvancedCount()
        => _advancedCount ??= await _advancedReminderManagement.CountAllAsync();

    private void RefreshAdvancedCacheIfExpired()
    {
        var now = DateTime.UtcNow;
        if (now < _advancedCacheExpiresUtc)
        {
            return;
        }

        _advancedPageTokens.Clear();
        _advancedCount = null;
        _advancedCacheExpiresUtc = now.AddSeconds(15);
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
