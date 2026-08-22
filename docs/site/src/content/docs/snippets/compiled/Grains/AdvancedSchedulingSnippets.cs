using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.DurableJobs;
using Orleans.Hosting;

namespace Documentation.Grains.AdvancedScheduling;

internal static class AdvancedReminderHosting
{
    internal static void ConfigureDevelopment(string[] args)
    {
        // <configure_in_memory_advanced_reminders>
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .UseInMemoryAdvancedReminderService();
});
        // </configure_in_memory_advanced_reminders>
    }

    internal static void ConfigureAzure(string[] args)
    {
        // <configure_azure_advanced_reminders>
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
var credential = new DefaultAzureCredential();

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .UseAzureTableAdvancedReminderService(options =>
        {
            options.TableServiceClient = new TableServiceClient(
                new Uri("https://contoso.table.core.windows.net"),
                credential);
            options.BlobServiceClient = new BlobServiceClient(
                new Uri("https://contoso.blob.core.windows.net"),
                credential);
            options.TableName = "OrleansAdvancedReminders";
            options.JobContainerName = "advanced-reminder-jobs";
        });

    siloBuilder.AddAdvancedReminders(options =>
    {
        options.MissedReminderGracePeriod = TimeSpan.FromMinutes(2);
        options.MaximumDeliveryAttempts = 3;
    });
});
        // </configure_azure_advanced_reminders>
    }

    internal static void ConfigureCleanup(ISiloBuilder siloBuilder)
    {
        // <configure_advanced_reminder_cleanup>
siloBuilder.AddAdvancedReminders(options =>
{
    // Treat an overdue occurrence as missed after this grace period.
    // Each reminder selects Skip, FireImmediately, or Notify when registered.
    options.MissedReminderGracePeriod = TimeSpan.FromMinutes(2);

    // Enable only when deployment policy makes an absent grain type retired.
    options.DeleteReminderWhenGrainTypeIsUnavailable = true;

    // Retry the same failing occurrence, then remove the registration.
    options.MaximumDeliveryAttempts = 3;
});
        // </configure_advanced_reminder_cleanup>
    }

    internal static void ConfigureMillionScaleCapacity(ISiloBuilder siloBuilder)
    {
        // <configure_advanced_reminder_capacity>
siloBuilder.Configure<DurableJobsOptions>(options =>
{
    // Split concentrated due-time windows across more journal shards.
    options.ShardDuration = TimeSpan.FromMinutes(5);
    options.ShardStripeCount = 16;
    options.MaxJobsPerShard = 10_000;

    // Bound persistence and execution pressure independently.
    options.MaxPendingOperationsPerShard = 2_048;
    options.MaxShardBatchOperationCount = 512;
    options.MaxShardBatchSizeBytes = 512 * 1024;
    options.MaxConcurrentJobsPerSilo = 2_000;
});
        // </configure_advanced_reminder_capacity>
    }
}

internal static class AdvancedReminderCronSchedules
{
    internal static void Build(TimeZoneInfo businessTimeZone)
    {
        // <build_advanced_reminder_cron_schedules>
ReminderCronBuilder everyMinute =
    ReminderCronBuilder.EveryMinute();

ReminderCronBuilder hourly =
    ReminderCronBuilder.HourlyAt(minute: 15);

ReminderCronBuilder dailyUtc =
    ReminderCronBuilder.DailyAt(
        new TimeOnly(hour: 2, minute: 30),
        TimeZoneInfo.Utc);

ReminderCronBuilder weekdays =
    ReminderCronBuilder.WeekdaysAt(
        new TimeOnly(hour: 9, minute: 0),
        businessTimeZone);

ReminderCronBuilder weekends =
    ReminderCronBuilder.WeekendsAt(
        new TimeOnly(hour: 11, minute: 0),
        businessTimeZone);

ReminderCronBuilder monday =
    ReminderCronBuilder.WeeklyOn(
        DayOfWeek.Monday,
        new TimeOnly(hour: 10, minute: 0),
        businessTimeZone);

ReminderCronBuilder monthStart =
    ReminderCronBuilder.MonthlyOn(
        dayOfMonth: 1,
        new TimeOnly(hour: 8, minute: 0),
        businessTimeZone);

ReminderCronBuilder monthEnd =
    ReminderCronBuilder.MonthlyOnLastDay(
        new TimeOnly(hour: 23, minute: 30),
        businessTimeZone);

ReminderCronBuilder annualReview =
    ReminderCronBuilder.YearlyOn(
        month: 3,
        dayOfMonth: 15,
        new TimeOnly(hour: 9, minute: 0),
        businessTimeZone);

// The year is ignored; this fires on February 29 only in leap years.
ReminderCronBuilder leapDay =
    ReminderCronBuilder.YearlyOn(
        new DateOnly(year: 2028, month: 2, day: 29),
        new TimeOnly(hour: 12, minute: 0),
        businessTimeZone);
        // </build_advanced_reminder_cron_schedules>

        _ = (
            everyMinute,
            hourly,
            dailyUtc,
            weekdays,
            weekends,
            monday,
            monthStart,
            monthEnd,
            annualReview,
            leapDay);
    }

    internal static void BuildAdvancedRules(TimeZoneInfo businessTimeZone)
    {
        // <build_advanced_reminder_cron_rules>
ReminderCronBuilder everyFiveMinutes =
    ReminderCronBuilder.EveryMinutes(interval: 5);

ReminderCronBuilder atSecondFifteen =
    ReminderCronBuilder.EveryMinuteAtSecond(second: 15);

ReminderCronBuilder mondayWednesdayFriday =
    ReminderCronBuilder.WeeklyOn(
        [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
        new TimeOnly(hour: 9, minute: 0))
    .InTimeZone(businessTimeZone);

ReminderCronBuilder nearestWeekday =
    ReminderCronBuilder.MonthlyOnNearestWeekday(
        dayOfMonth: 1,
        new TimeOnly(hour: 9, minute: 0))
    .InTimeZone(businessTimeZone);

ReminderCronBuilder threeDaysBeforeMonthEnd =
    ReminderCronBuilder.MonthlyBeforeLastDay(
        daysBeforeLastDay: 3,
        new TimeOnly(hour: 9, minute: 0))
    .InTimeZone(businessTimeZone);

ReminderCronBuilder lastFriday =
    ReminderCronBuilder.MonthlyOnLast(
        DayOfWeek.Friday,
        new TimeOnly(hour: 9, minute: 0))
    .InTimeZone(businessTimeZone);

ReminderCronBuilder secondMonday =
    ReminderCronBuilder.MonthlyOnNth(
        DayOfWeek.Monday,
        occurrence: 2,
        new TimeOnly(hour: 9, minute: 0))
    .InTimeZone(businessTimeZone);

ReminderCronBuilder selectedMonths =
    ReminderCronBuilder.WeekdaysInMonthsAt(
        months: [1, 3],
        new TimeOnly(hour: 9, minute: 30, second: 15))
    .InTimeZone(businessTimeZone);
        // </build_advanced_reminder_cron_rules>

        _ = (
            everyFiveMinutes,
            atSecondFifteen,
            mondayWednesdayFriday,
            nearestWeekday,
            threeDaysBeforeMonthEnd,
            lastFriday,
            secondMonday,
            selectedMonths);
    }

    internal static void BuildCompleteCronGrammar(TimeZoneInfo businessTimeZone)
    {
        // <build_advanced_reminder_cron_fields>
// At second 20 of every fifth minute during weekday business hours.
ReminderCronBuilder officePolling = ReminderCronBuilder.FromFields(
    second: ReminderCronSecond.At(20),
    minute: ReminderCronMinute.Every(interval: 5),
    hour: ReminderCronHour.Range(start: 9, end: 17),
    dayOfMonth: ReminderCronDayOfMonth.Any,
    month: ReminderCronMonth.Any,
    dayOfWeek: ReminderCronDayOfWeek.Range(
        DayOfWeek.Monday,
        DayOfWeek.Friday))
    .InTimeZone(businessTimeZone);

// Lists can combine individual values, ranges, and stepped ranges.
ReminderCronBuilder mixedMinuteList = ReminderCronBuilder.FromFields(
    minute: ReminderCronMinute.Combine(
        ReminderCronMinute.At(3),
        ReminderCronMinute.EveryBetween(start: 5, end: 11, interval: 3),
        ReminderCronMinute.At(12)),
    hour: ReminderCronHour.At(1),
    dayOfMonth: ReminderCronDayOfMonth.Any,
    month: ReminderCronMonth.Any,
    dayOfWeek: ReminderCronDayOfWeek.Any);

// Both fields are constraints, so this means Friday the 13th.
ReminderCronBuilder fridayTheThirteenth = ReminderCronBuilder.FromFields(
    minute: ReminderCronMinute.At(0),
    hour: ReminderCronHour.At(9),
    dayOfMonth: ReminderCronDayOfMonth.On(13),
    month: ReminderCronMonth.Any,
    dayOfWeek: ReminderCronDayOfWeek.On(DayOfWeek.Friday));

ReminderCronBuilder lastWeekday = ReminderCronBuilder.FromFields(
    minute: ReminderCronMinute.At(0),
    hour: ReminderCronHour.At(9),
    dayOfMonth: ReminderCronDayOfMonth.LastWeekday,
    month: ReminderCronMonth.Any,
    dayOfWeek: ReminderCronDayOfWeek.Any);

ReminderCronBuilder fiveDaysBeforeMonthEndOnNearestWeekday =
    ReminderCronBuilder.FromFields(
        minute: ReminderCronMinute.At(0),
        hour: ReminderCronHour.At(9),
        dayOfMonth: ReminderCronDayOfMonth.NearestWeekdayBeforeLast(offset: 5),
        month: ReminderCronMonth.Any,
        dayOfWeek: ReminderCronDayOfWeek.Any);
        // </build_advanced_reminder_cron_fields>

        _ = (
            officePolling,
            mixedMinuteList,
            fridayTheThirteenth,
            lastWeekday,
            fiveDaysBeforeMonthEndOnNearestWeekday);
    }

    internal static void BuildRawCronExpressions(TimeZoneInfo businessTimeZone)
    {
        // <build_advanced_reminder_raw_cron>
ReminderCronBuilder configuredSchedule = ReminderCronBuilder
    .FromExpression("0 3,5-11/3,12 1 * * *")
    .InTimeZone(businessTimeZone);

// Build validates the five- or six-field expression immediately.
ReminderCronExpression validatedExpression = configuredSchedule.Build();

ReminderCronBuilder monthlyMacro =
    ReminderCronBuilder.FromExpression("@monthly");
        // </build_advanced_reminder_raw_cron>

        _ = (validatedExpression, monthlyMacro);
    }

    internal static ReminderSchedule BuildEveryOtherMonday()
    {
        // <build_advanced_reminder_biweekly_schedule>
var firstMondayUtc = new DateTime(
    year: 2030,
    month: 1,
    day: 7,
    hour: 9,
    minute: 0,
    second: 0,
    DateTimeKind.Utc);

ReminderSchedule everyOtherMonday = ReminderSchedule.Interval(
    firstMondayUtc,
    TimeSpan.FromDays(14));
        // </build_advanced_reminder_biweekly_schedule>

        return everyOtherMonday;
    }
}

public interface IReportGrain : IGrainWithStringKey
{
    Task ConfigureWeekdayReportAsync();

    Task ScheduleExpirationAsync();
}

public interface IDeclarativeReportGrain : IGrainWithStringKey;

// <register_advanced_reminders_with_attributes>
[RegisterReminder(
    "health-poll",
    dueSeconds: 10,
    periodSeconds: 300,
    priority: DurableJobPriority.Normal,
    action: MissedReminderAction.Skip)]
[RegisterReminder(
    "weekday-report",
    "0 9 * * MON-FRI",
    priority: DurableJobPriority.High,
    action: MissedReminderAction.FireImmediately)]
public sealed class DeclarativeReportGrain :
    Grain,
    IDeclarativeReportGrain,
    Orleans.AdvancedReminders.IRemindable
{
    public Task ReceiveReminder(
        string reminderName,
        Orleans.AdvancedReminders.Runtime.TickStatus status)
    {
        return reminderName switch
        {
            "health-poll" => PollHealthAsync(),
            "weekday-report" => GenerateReportAsync(),
            _ => Task.CompletedTask
        };
    }

    private Task PollHealthAsync() => Task.CompletedTask;

    private Task GenerateReportAsync() => Task.CompletedTask;
}
// </register_advanced_reminders_with_attributes>

// <advanced_reminder_grain>
public sealed class ReportGrain :
    Grain,
    IReportGrain,
    Orleans.AdvancedReminders.IRemindable
{
    // <register_advanced_reminder_cron>
    public Task ConfigureWeekdayReportAsync()
    {
        return this.RegisterOrUpdateAdvancedReminder(
            "weekday-report",
            ReminderCronBuilder
                .WeekdaysAt(new TimeOnly(hour: 9, minute: 0))
                .InTimeZone("Europe/Kyiv"),
            DurableJobPriority.High,
            MissedReminderAction.FireImmediately);
    }
    // </register_advanced_reminder_cron>

    // <register_advanced_reminder_one_shot_date>
    public Task ScheduleExpirationAsync()
    {
        var expiresAt = new DateTimeOffset(
            year: 2030,
            month: 4,
            day: 15,
            hour: 16,
            minute: 30,
            second: 0,
            offset: TimeSpan.Zero);

        return this.RegisterOrUpdateAdvancedReminder(
            "expire",
            ReminderSchedule.OneShot(expiresAt),
            DurableJobPriority.High,
            MissedReminderAction.FireImmediately);
    }
    // </register_advanced_reminder_one_shot_date>

    public Task ReceiveReminder(
        string reminderName,
        Orleans.AdvancedReminders.Runtime.TickStatus status)
    {
        return reminderName switch
        {
            "weekday-report" => GenerateReportAsync(),
            "expire" => ExpireAsync(),
            _ => Task.CompletedTask
        };
    }

    private Task GenerateReportAsync() => Task.CompletedTask;

    private Task ExpireAsync() => Task.CompletedTask;
}
// </advanced_reminder_grain>

internal static class AdvancedReminderManagement
{
    // <query_advanced_reminders>
internal static Task<ReminderManagementPage> ListOverdueAsync(
    IGrainFactory grainFactory,
    string? continuationToken)
{
    IReminderManagementGrain management =
        grainFactory.GetReminderManagementGrain();

    return management.ListFilteredAsync(
        new ReminderQueryFilter
        {
            Status = ReminderQueryStatus.Overdue,
            OverdueBy = TimeSpan.FromMinutes(15),
            Priority = DurableJobPriority.High
        },
        pageSize: 100,
        continuationToken);
}
    // </query_advanced_reminders>

    // <repair_or_delete_advanced_reminders>
internal static async Task CleanRetiredTypeAsync(
    IGrainFactory grainFactory,
    Orleans.Runtime.GrainType retiredType,
    Func<Orleans.AdvancedReminders.ReminderEntry, bool> isConfirmedRetired,
    CancellationToken cancellationToken)
{
    IReminderManagementGrain management =
        grainFactory.GetReminderManagementGrain();

    var filter = new ReminderQueryFilter { GrainType = retiredType };
    await foreach (Orleans.AdvancedReminders.ReminderEntry reminder in management.EnumerateFilteredAsync(
        filter,
        pageSize: 100,
        cancellationToken))
    {
        if (isConfirmedRetired(reminder))
        {
            await management.DeleteAsync(
                reminder.GrainId,
                reminder.ReminderName);
        }
    }
}

internal static Task RepairNextDueAsync(
    IGrainFactory grainFactory,
    Orleans.AdvancedReminders.ReminderEntry reminder)
{
    return grainFactory
        .GetReminderManagementGrain()
        .RepairAsync(reminder.GrainId, reminder.ReminderName);
}
    // </repair_or_delete_advanced_reminders>
}
