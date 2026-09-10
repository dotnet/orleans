# Microsoft Orleans Advanced Reminders

Advanced Reminders schedules durable callbacks to Orleans grains using intervals, one-shot deadlines, or time-zone-aware cron expressions. Registrations belong to a grain identity and can activate an inactive grain when work is due. Multiple named reminders can run independently for the same grain.

The package provides programmatic and attribute-based registration, missed-occurrence policies, paged administration, and Dashboard integration. It uses the existing Orleans Durable Jobs APIs for scheduling, cancellation, and delivery. Classic reminders keep their own API; Advanced Reminders uses explicitly named extensions such as `RegisterOrUpdateAdvancedReminder`.

This package family is prerelease. Delivery is at least once: use application-level operation identities and idempotent side effects. A one-shot schedule describes one intended occurrence, not an exactly-once callback guarantee.

## Feature guide

| Capability | Examples and behavior |
| --- | --- |
| Interval, UTC anchor, and one-shot schedules | [Registration and time forms](#register-intervals-one-shot-deadlines-and-cron-schedules) |
| Cron factories, typed fields, raw expressions, and occurrence queries | [Cron examples](#cron-examples) |
| Time zones, offset-aware inputs, leap days, and daylight-saving transitions | [Time zones and clock changes](#time-zones-and-clock-changes) |
| Automatic interval and UTC cron registration | [Attributes](#register-with-attributes) |
| Skip, immediate delivery, and logged missed notifications | [Missed occurrences](#missed-occurrences-retries-and-cleanup) |
| Callback retries and retired-grain cleanup | [Options and failure behavior](#missed-occurrences-retries-and-cleanup) |
| Filtered paging, async enumeration, repair, deletion, and Dashboard | [Administration](#inspect-repair-and-delete-reminders) |
| Persistent providers, coexistence with classic reminders, and recovery | [Storage setup](#configure-a-silo) and [delivery](#delivery-and-recovery) |

## Configure a silo

Install `Microsoft.Orleans.AdvancedReminders` and configure a reminder definition provider. For local development:

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(silo => silo
    .UseLocalhostClustering()
    .UseInMemoryAdvancedReminderService());

await builder.Build().RunAsync();
```

`UseInMemoryAdvancedReminderService()` configures both in-memory definitions and in-memory Durable Jobs. Neither survives a full cluster restart.

For production, persist **both** reminder definitions and Durable Jobs. A persistent reminder table alone is insufficient:

| Definition storage | Package | Silo extension | Job storage |
| --- | --- | --- | --- |
| Azure Table Storage | `Microsoft.Orleans.AdvancedReminders.AzureStorage` | `UseAzureTableAdvancedReminderService` | Configures Azure Blob Durable Jobs; supply table and blob clients. |
| Cosmos DB | `Microsoft.Orleans.AdvancedReminders.Cosmos` | `UseCosmosAdvancedReminderService` | Configure a Durable Jobs backend separately. |
| DynamoDB | `Microsoft.Orleans.AdvancedReminders.DynamoDB` | `UseDynamoDBAdvancedReminderService` | Configure a Durable Jobs backend separately. |
| Redis | `Microsoft.Orleans.AdvancedReminders.Redis` | `UseRedisAdvancedReminderService` | Configure a Durable Jobs backend separately. |
| SQL Server, PostgreSQL, MySQL/MariaDB, Oracle | `Microsoft.Orleans.AdvancedReminders.AdoNet` | `UseAdoNetAdvancedReminderService` | Apply the database's advanced-reminder SQL script and configure Durable Jobs separately. |

There is currently no Advanced Reminders Firestore provider. Configure clustering and any grain-state persistence separately from reminder storage.

## Register intervals, one-shot deadlines, and cron schedules

The grain implements `Orleans.AdvancedReminders.IRemindable`. This example includes the grain interface, registration, callback, and removal:

```csharp
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

public interface IWorkGrain : IGrainWithStringKey
{
    Task StartPollingAsync();
    Task ScheduleDeadlineAsync(DateTimeOffset deadline);
    Task ScheduleReportAsync();
    Task StopAsync(string name);
}

public sealed class WorkGrain : Grain, IWorkGrain,
    Orleans.AdvancedReminders.IRemindable
{
    public Task StartPollingAsync() => this.RegisterOrUpdateAdvancedReminder(
        "poll", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5),
        MissedReminderAction.Skip);

    public Task ScheduleDeadlineAsync(DateTimeOffset deadline)
        => this.RegisterOrUpdateAdvancedReminder(
            "deadline", deadline, MissedReminderAction.FireImmediately);

    public Task ScheduleReportAsync() => this.RegisterOrUpdateAdvancedReminder(
        "report",
        ReminderCronBuilder.WeekdaysAt(new TimeOnly(9, 30))
            .InTimeZone("Europe/Kyiv"),
        MissedReminderAction.FireImmediately);

    public async Task StopAsync(string name)
    {
        var reminder = await this.GetAdvancedReminder(name);
        if (reminder is not null)
        {
            await this.UnregisterAdvancedReminder(reminder);
        }
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // Replace this with idempotent application work for poll/deadline/report.
        Console.WriteLine($"{reminderName}: delivered at {status.CurrentTickTime:O}");
        return Task.CompletedTask;
    }
}
```

Registering the same name updates its schedule. Different names create independent registrations. `GetAdvancedReminders()` lists the current grain's handles. Equivalent registration overloads are available for `IGrainBase`, and registry/service extensions accept an explicit `GrainId`.

`TickStatus.FirstTickTime` is the registration's start time, `CurrentTickTime` is the delivery time, and `Period` is zero for cron and one-shot schedules. These values do not provide an application-level deduplication key.

### Choose the schedule form

| Requirement | Schedule |
| --- | --- |
| Once after a delay | `ReminderSchedule.OneShot(TimeSpan.FromMinutes(30))` |
| Once at an offset-aware timestamp | `ReminderSchedule.OneShot(deadline)` where `deadline` is `DateTimeOffset` |
| Once at a UTC timestamp | `ReminderSchedule.OneShot(dueAtUtc)` where `dueAtUtc.Kind` is `Utc` |
| Repeat after an initial delay | `ReminderSchedule.Interval(TimeSpan.Zero, TimeSpan.FromMinutes(5))` |
| Repeat from a fixed UTC anchor | `ReminderSchedule.Interval(firstDueUtc, TimeSpan.FromDays(14))` |
| Follow a local calendar | `ReminderCronBuilder.DailyAt(9, 30).InTimeZone("Europe/Paris")` |
| Read cron from configuration | `ReminderSchedule.Cron("30 9 * * TUE-FRI", "Asia/Dubai")` |

One-shot convenience overloads accept all three time types directly. The following methods are alternatives for the same named deadline:

```csharp
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

public static class DeadlineRegistration
{
    public static Task<IGrainReminder> AfterDelay(Grain grain)
        => grain.RegisterOrUpdateAdvancedReminder("deadline",
            TimeSpan.FromMinutes(30), MissedReminderAction.FireImmediately);

    public static Task<IGrainReminder> AtUtc(Grain grain, DateTime dueAtUtc)
        => grain.RegisterOrUpdateAdvancedReminder("deadline",
            dueAtUtc, MissedReminderAction.FireImmediately);

    public static Task<IGrainReminder> AtOffset(Grain grain, DateTimeOffset dueAt)
        => grain.RegisterOrUpdateAdvancedReminder("deadline",
            dueAt, MissedReminderAction.FireImmediately);
}
```

`DateTimeOffset` identifies an instant and is normalized to UTC. `DateTime` requires `DateTimeKind.Utc`. The grain passed to these methods must implement the advanced `IRemindable` interface. A completed or skipped one-shot is removed; callbacks can replace or unregister their own registration without that change being overwritten by completion.

### One-shot completion and verification

After a successful callback, the service removes the one-shot definition and schedules no next occurrence. A stale delivery after that removal does nothing. Registering the same name again deliberately creates a new occurrence. The default `ReminderRetryPolicy.Limited(3)` retries a failing occurrence and removes the registration when delivery still fails on the third dequeue. Select `NoRetries` to complete a failing one-shot immediately, or `UntilSuccess` to retain it for delayed delivery until success or explicit removal.

The delivery regression `OneShotReminder_DoesNotRepeatAfterCompletion` covers relative delay, UTC `DateTime`, and nonzero-offset `DateTimeOffset`: it delivers the reminder, advances the clock by 30 days, and redelivers the old job as both an ordinary delivery and a retry. It asserts one callback, one original scheduled job, one removal, and no remaining registration. Separate missed-occurrence and retry tests cover skipped/notify one-shots, overdue immediate delivery, callback failures, and replacement from a callback. These checks verify completion behavior; they do not turn at-least-once delivery into exactly-once business effects.

## Cron examples

Use a builder for common calendars, typed fields for composition, or a raw expression for configuration:

```csharp
using Orleans.AdvancedReminders;

var morning = ReminderCronBuilder.DailyAt(9, 30).InTimeZone("Europe/Paris");
var weekdays = ReminderCronBuilder.WeekdaysAt(9, 30).InTimeZone("Europe/Vienna");
var monthEnd = ReminderCronBuilder.MonthlyOnLastDay(new TimeOnly(23, 30))
    .InTimeZone("Asia/Dubai");
var lastFriday = ReminderCronBuilder.MonthlyOnLast(
    DayOfWeek.Friday, new TimeOnly(9, 0)).InTimeZone("Australia/Sydney");

var tuesdayToFriday = ReminderCronBuilder.FromExpression("30 9 * * TUE-FRI")
    .InTimeZone("Europe/Kyiv");

// Thursday, Saturday, Monday, Wednesday, at 09:30 local time.
var wrappedWeek = ReminderCronBuilder.FromFields(
    ReminderCronMinute.At(30), ReminderCronHour.At(9),
    ReminderCronDayOfMonth.Any, ReminderCronMonth.Any,
    ReminderCronDayOfWeek.EveryBetween(DayOfWeek.Thursday, DayOfWeek.Wednesday, 2))
    .InTimeZone("Australia/Lord_Howe");

// Validate configuration-provided syntax before registering it.
var parsed = tuesdayToFriday.ToCronExpression();
var from = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
DateTimeOffset? next = tuesdayToFriday.GetNextOccurrence(from);
var week = tuesdayToFriday.GetOccurrences(from, from.AddDays(7)).ToArray();
```

| Expression | Meaning |
| --- | --- |
| `*/5 * * * *` | Every fifth minute within each hour. |
| `15 */5 * * * *` | The same cadence at second 15. |
| `30 9 * * TUE-FRI` | Tuesday through Friday at 09:30. |
| `30 9 * * THU-WED` | All seven days, wrapping through Sunday. |
| `30 9 * * THU-WED/2` | Thursday, Saturday, Monday, Wednesday. |
| `0 23 L * *` | Last calendar day of each month at 23:00. |
| `0 9 15W * *` | Weekday nearest the 15th at 09:00. |
| `0 9 * * MON#2` | Second Monday of the month at 09:00. |
| `0 9 * * FRIL` | Last Friday of the month at 09:00. |
| `0 12 29 2 *` | February 29 at noon in leap years. |

Five fields mean minute/hour/day/month/weekday; six fields add seconds first. Lists, inclusive and wrapping ranges, steps, named months/weekdays, `L`, `L-n`, `W`, `LW`, `#`, and macros such as `@daily` are supported. Names are case-insensitive; Sunday is `0` or `7`. Restricted day-of-month and weekday fields must **both** match.

Steps select positions within a field, not elapsed durations: `*/7` minutes resets each hour. Use an interval with a UTC anchor for an elapsed cadence such as every 14 days. The default `MinimumReminderPeriod` is one minute and applies to interval and cron registration; second-level syntax does not bypass that limit.

### Time zones and clock changes

Cron defaults to UTC. For local schedules use an installed system time-zone ID; availability and historical rules depend on the host database. A query's `DateTimeOffset` does not change the schedule's time zone. Offset-aware query results use offset zero; `DateTime` query inputs must be UTC.

Forward clock changes collapse skipped occurrences into one occurrence at the first valid whole second. During a rollback, a fixed clock fires once at the earlier UTC instant; wildcard, range, or step **clock fields** also run through the repeated interval. A weekday range alone does not enable repeated-clock delivery.

For `30 2 * * TUE-FRI`, a Wednesday jump from 02:00 to 03:00 makes that Wednesday occurrence due at 03:00 local time. The weekday range selects the intended local day. These are scheduling instants; actual execution can be later and follows the missed-occurrence policy.

`GetNextOccurrence` excludes the starting instant by default. `GetOccurrences` includes the start and excludes the end by default; inclusion flags override either boundary. Results increase in UTC, and a valid but impossible calendar such as February 31 returns no occurrence.

## Register with attributes

Apply one or more `[RegisterReminder]` attributes to the grain class:

```csharp
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

public interface IAutomaticReportGrain : IGrainWithStringKey { }

[RegisterReminder("poll", dueSeconds: 10, periodSeconds: 300)]
[RegisterReminder("report", "30 9 * * TUE-FRI",
    action: MissedReminderAction.FireImmediately)]
public sealed class AutomaticReportGrain : Grain, IAutomaticReportGrain,
    Orleans.AdvancedReminders.IRemindable
{
    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // Dispatch by name and perform idempotent poll/report work.
        Console.WriteLine($"{reminderName}: {status.CurrentTickTime:O}");
        return Task.CompletedTask;
    }
}
```

Attributes are reconciled on activation. An unchanged declaration preserves its current occurrence; missing or changed registrations are registered again. The grain must first activate for declarations to be registered. Removing an attribute does not delete an existing registration: unregister it explicitly.

Attribute intervals use seconds, and attribute cron schedules use **UTC**. Attributes currently have no time-zone or one-shot overload: use programmatic registration for those forms. The period must be positive, so `periodSeconds: 0` is not an attribute-based one-shot.

## Missed occurrences, retries, and cleanup

An occurrence is missed when its lateness exceeds `ReminderOptions.MissedReminderGracePeriod` (30 seconds by default):

| Policy | Missed occurrence |
| --- | --- |
| `Skip` (default) | Skip its callback and advance the recurring schedule; remove a one-shot. |
| `FireImmediately` | Invoke once when processed, then advance; does not replay every missed interval. |
| `Notify` | Log a warning and skip the callback; there is no separate notification callback. |

### Read missed notifications

`Notify` writes a structured warning in the category `Orleans.AdvancedReminders.Runtime.ReminderService.AdvancedReminderService`. Its fields are `ReminderName`, `GrainId`, `Due`, and `Now`; its message contains `missed due window`. Configure the silo's logging provider to retain that category at `Warning` or lower. For example, create the host with JSON console logging, then configure Orleans and build it as above:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class ReminderLogging
{
    public static HostApplicationBuilder CreateHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddJsonConsole();
        builder.Logging.AddFilter(
            "Orleans.AdvancedReminders.Runtime.ReminderService.AdvancedReminderService",
            LogLevel.Warning);
        return builder;
    }
}
```

Read those records from the console or the logging backend collecting silo output, and use the fields to correlate the reminder and the missed due time. `Notify` does not invoke `ReceiveReminder` and does not publish a separate notification event. Historical notifications are retained by your logging system.

`ReminderQueryStatus.Missed` queries current registrations whose due time is overdue by at least the query's `MissedBy` threshold and whose last delivery is absent or earlier than that due time. It isn't event history: after a recurring reminder advances or a missed one-shot is removed, the old missed occurrence is no longer in that query. `MissedBy` is independent of the configured delivery grace period.

### Configure retries and cleanup

Configure service-wide limits while building the silo:

```csharp
using Orleans.AdvancedReminders;
using Orleans.Hosting;

public static class ReminderPolicies
{
    public static void Configure(ISiloBuilder silo) => silo.AddAdvancedReminders(options =>
    {
        options.MinimumReminderPeriod = TimeSpan.FromMinutes(1);
        options.MissedReminderGracePeriod = TimeSpan.FromMinutes(2);
        options.DeliveryRetryPolicy = ReminderRetryPolicy.Limited(3);
        options.DeleteReminderWhenGrainTypeIsUnavailable = false;
    });
}
```

`InitializationTimeout` bounds service initialization and defaults to five minutes.

`DeliveryRetryPolicy` is one immutable policy. Its default is `ReminderRetryPolicy.Limited(3)`, including the initial attempt. Choose the behavior explicitly:

| Policy | Behavior after a delivery failure |
| --- | --- |
| `ReminderRetryPolicy.NoRetries` | Log the failure, advance a recurring schedule, or remove a one-shot. |
| `ReminderRetryPolicy.Limited(attempts)` | Retry the same Durable Job occurrence and remove the registration if delivery still fails at the configured dequeue count. |
| `ReminderRetryPolicy.UntilSuccess` | Retain the failed work and schedule delayed deliveries until success or explicit removal. |

`Limited` requires a positive attempt count; zero and negative counts are rejected when constructing the policy. Its count is a Durable Jobs dequeue-count safety limit, not an exact callback-exception count. Configure the backend retry policy to allow at least as many attempts. Configuration binding accepts the strings `NoRetries`, `Limited(3)`, and `UntilSuccess` for the `DeliveryRetryPolicy` property.

For a recurring reminder, `NoRetries` advances after a failed tick, while `Limited(1)` removes the registration after its first failed delivery. Both allow the initial callback; the policy determines what happens after failure.

`DeleteReminderWhenGrainTypeIsUnavailable` defaults to `false`. Enabling it deletes a due reminder only after a stable, complete cluster manifest shows no active silo declaring its grain type. A grain type can be temporarily absent during deployment, so enable this only when absence means retirement in your deployment policy.

### Retain failed deliveries

Select `ReminderRetryPolicy.UntilSuccess` when scheduled work must survive delivery failures, including failures before the recipient enters its callback:

```csharp
using Orleans.AdvancedReminders;
using Orleans.Hosting;

public static class RetainedReminderDelivery
{
    public static void Configure(ISiloBuilder silo) => silo.AddAdvancedReminders(options =>
    {
        options.DeliveryRetryPolicy = ReminderRetryPolicy.UntilSuccess;
        options.MinimumReminderPeriod = TimeSpan.FromMinutes(1);
    });
}
```

With `UntilSuccess`, each failure persists a fresh occurrence and schedules a new Durable Job after `MinimumReminderPeriod`, measured from completion of the failed delivery. The new job has its own dequeue count, so repeated failures can outlive the backend's per-job retry limit. Monitor repeated errors and overdue work; persistent failure continues until success or explicit removal.

One-shots preserve the original `StartAt` deadline and move `NextDueUtc` to the retry time. Successful delivery removes them. Interval and cron schedules retain their logical `NextDueUtc` while the native job waits for its retry time. After success, they advance to the next future slot on the original interval anchor or cron calendar, coalescing elapsed slots.

Recovery repairs a missing handle and rotates the occurrence identity to fence orphaned jobs; retained recurring work receives another delayed attempt. An attempted occurrence remains retryable after the missed grace period expires. `Skip` and `Notify` still apply to an initial occurrence which was never attempted. Request cancellation leaves the occurrence unchanged, and registration replacement or removal wins over a late callback result.

Use `ReminderRetryPolicy.Limited(attempts)` when delivery must stop at a configured attempt limit. Make business effects idempotent using a stable application work identifier across attempts: each retained retry has a new job ID, and a lost response can cause delivery to repeat after an effect committed.

## Inspect, repair, and delete reminders

Administrative APIs use bounded pages and opaque continuation tokens:

```csharp
using Orleans;
using Orleans.AdvancedReminders;

public static class ReminderAdministration
{
    public static Task<ReminderManagementPage> FindOverdueAsync(
        IGrainFactory grains, string? continuationToken = null)
        => grains.GetReminderManagementGrain().ListFilteredAsync(
            new ReminderQueryFilter
            {
                Status = ReminderQueryStatus.Overdue,
                OverdueBy = TimeSpan.FromMinutes(15)
            },
            pageSize: 100,
            continuationToken: continuationToken);
}
```

Pass the returned continuation token to the same query for the next page. Management also supports `ListAllAsync`, `ListForGrainAsync`, `ListDueInRangeAsync`, `SetActionAsync`, `RepairAsync`, and `DeleteAsync`. Filters include grain type, schedule kind, missed policy, due bounds, and due/overdue/missed/upcoming status. Due-range management bounds are inclusive UTC timestamps. `EnumerateFilteredAsync` provides asynchronous iteration over matching pages.

Pages use stable storage-hash-bucket order, not global due-time order, and do not represent a snapshot across concurrent changes. Grain-type filtering is bounded but still scans storage. `RepairAsync` recalculates the next due time; it is not a force-fire API. The Orleans Dashboard also exposes advanced reminder inspection and management. It displays the store's total registration count, cached for 30 seconds across page requests. The count and page follow the provider's read consistency and can change during concurrent writes. ADO.NET deployments created with an earlier script must add its `AdvancedRemindersCountKey` query to `OrleansQuery` before upgrading.

## Delivery and recovery

Definitions record the schedule, missed policy, next due time, and occurrence identity. Durable Jobs holds the scheduled delivery. Far-future occurrences are scheduled immediately; recurring schedules expand one occurrence at a time. Recovery repairs incomplete job handles through bounded reminder-table pages. It does not inspect Durable Jobs journals or replace its time/silo partitioning, batching, and retries.

A normal dispatcher turn prepares each delivery and admits one callback at a time for its recipient. The callback is awaited outside that normal turn, allowing independent recipient turns to update or unregister reminders without waiting on their own queued callback. A normal completion turn rechecks the original ETag and schedule identity so an old occurrence cannot overwrite its replacement. Each attempt chooses its admission token before preparation, so cancellation or a lost preparation reply can release only that attempt without advancing its occurrence. Cancellation observed before callback dispatch prevents that callback from starting. Neither successful scheduling nor a passing cron query promises exact wall-clock callback execution during outages or load.

Replacing or unregistering a reminder commits its definition change before attempting to cancel the previous durable job. This includes administrative mutations and changed attribute declarations. This cleanup is best-effort and waits at most five seconds, including when the job backend ignores request cancellation. A failed or timed-out cleanup is logged; it does not undo the committed change or prevent subsequent dispatcher requests. Any stale occurrence remains fenced by the persisted schedule identity.

Reminder-table reads in the service, dispatcher, management grain, and recovery scan have a one-minute timeout. Dispatcher reads also honor request cancellation. A late read response is discarded and cannot resume the abandoned mutation. A failed recovery page leaves its hash range incomplete so the next heartbeat retries that range.

Creating a durable job also has a one-minute timeout and honors cancellation even when the backend ignores the token. The committed definition remains pending if scheduling fails. Recovery rotates its occurrence identity before rescheduling, so a job created by an abandoned scheduling request is harmless. Recovery bounds each dispatcher repair to one minute, and the silo's shutdown token bounds the wait for the reminder provider to stop.

Reminder-table writes remain awaited until the provider finishes, preserving the dispatcher's single-writer ordering across a commit. Configure storage SDK request deadlines for those writes; stopping observation alone cannot prevent a late write from overwriting a newer registration. Callback execution uses Orleans messaging deadlines, and cancellation cannot forcibly stop application callback code.

For the full walkthrough and operational reference, see the [Advanced Reminders documentation](../../docs/site/src/content/docs/grains/advanced-reminders.md).
