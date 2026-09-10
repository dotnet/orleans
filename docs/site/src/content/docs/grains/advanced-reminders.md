---
title: Schedule work with advanced reminders
description: Configure durable interval, one-shot, and cron callbacks with operational controls.
ms.date: 09/10/2026
ms.topic: how-to
---

# Schedule work with advanced reminders

Advanced Reminders is an opt-in reminder stack which uses Orleans Durable Jobs for delivery. It adds one-shot and cron schedules, absolute UTC due times, missed-occurrence policies, administrative paging, and cleanup guardrails. It uses separate packages, namespaces, storage, and hosting methods from classic Orleans reminders, so both stacks can run in the same application.

> [!IMPORTANT]
> Advanced Reminders is prerelease. Don't point classic and advanced providers at the same storage table or key prefix. Migrating an existing reminder definition requires an application-controlled cutover; enabling Advanced Reminders doesn't import classic registrations.

## Feature guide

| Capability | Guide |
| --- | --- |
| Relative intervals and absolute UTC first occurrences | [Registration](#implement-and-register-a-reminder) |
| One-shot delay, UTC date, and offset-aware date | [One-shot work](#schedule-one-shot-work) |
| Cron factories, typed fields, expressions, ranges, and occurrence queries | [Cron schedules](#build-cron-schedules) |
| Time zones, leap dates, and daylight-saving transitions | [Seasonal clock changes](#seasonal-clock-changes) |
| Activation-time interval and UTC cron declarations | [Attribute registration](#register-reminders-with-attributes) |
| Skip, immediate delivery, and logged missed notifications | [Missed-occurrence behavior](#select-missed-occurrence-behavior) |
| Current missed registrations and historical notification diagnosis | [Read missed notifications](#read-missed-notifications) |
| Filtered paging, repair, policy changes, deletion, and Dashboard | [Management](#query-and-repair-registrations) |
| Callback retry limits and retired-grain cleanup | [Cleanup guardrails](#configure-cleanup-guardrails) |
| Provider choices, recovery, and coexistence with classic reminders | [Storage](#understand-the-storage-model) and [delivery](#understand-delivery-and-time-buckets) |

## Choose a reminder stack

| Requirement | Classic reminders | Advanced Reminders |
| --- | --- | --- |
| Durable recurring interval | Yes | Yes |
| One-shot registration | Unregister after a regular tick | Native one-shot schedule |
| Absolute UTC first occurrence | No | Yes |
| Five- or six-field cron and time zones | No | Yes |
| Missed-occurrence policy | No | Yes |
| Paged administrative queries | Per-grain APIs | Cluster-wide filtered paging |
| Delivery scheduling | Reminder service | Durable Jobs |

Use [classic reminders](timers-and-reminders.md#reminders) when their interval model and occasional missed-tick semantics are sufficient. Use Advanced Reminders when the schedule or operational controls require the additional behavior. Neither reminder stack is intended for high-frequency work; use a [grain timer](timers-and-reminders.md#grain-timers) for activation-scoped frequent callbacks.

## Understand the storage model

Every Advanced Reminders deployment requires two durable roles:

1. A reminder table stores the target grain, reminder name, schedule, next due time, missed-occurrence policy, and concurrency token.
1. A Durable Jobs backend stores the distributed job which wakes the reminder dispatcher at the next due time.

The dispatcher re-reads the reminder and validates its schedule identity before invoking the grain. Updating or unregistering a reminder therefore prevents a stale queued job from restoring or firing the replaced registration. Recurring reminders persist their next occurrence and schedule a new durable job after each due occurrence.

The in-memory option supplies both roles for local development. The Azure Storage provider supplies Azure Table Storage for reminder definitions and Azure Blob Storage for Durable Jobs. ADO.NET, Cosmos DB, DynamoDB, and Redis providers store definitions only and require a separately configured Durable Jobs backend.

| Reminder package | Definition store | Durable Jobs configuration |
| --- | --- | --- |
| `Microsoft.Orleans.AdvancedReminders` | Volatile memory | Included by `UseInMemoryAdvancedReminderService()`; development only |
| `Microsoft.Orleans.AdvancedReminders.AzureStorage` | Azure Table Storage | Azure Blob Storage configured by the same provider |
| `Microsoft.Orleans.AdvancedReminders.AdoNet` | SQL Server, PostgreSQL, MySQL/MariaDB, or Oracle | Configure separately |
| `Microsoft.Orleans.AdvancedReminders.Cosmos` | Azure Cosmos DB | Configure separately |
| `Microsoft.Orleans.AdvancedReminders.DynamoDB` | Amazon DynamoDB | Configure separately |
| `Microsoft.Orleans.AdvancedReminders.Redis` | Redis | Configure separately |

Firestore currently has a classic reminder provider only.

For production, don't pair a durable reminder table with in-memory Durable Jobs. The definitions would survive a cluster stop while the jobs which deliver them would not. After losing the job store, explicitly repair affected registrations to replace their persisted handles. A persistent backend is the intended production topology.

The ADO.NET schema includes a composite `(ServiceId, GrainHash)` index used by recovery and administrative range scans. Apply the advanced-reminder schema shipped for the selected database before enabling the provider. If a prerelease deployment created the table from an older script, add that index during the upgrade; the `(ServiceId, NextDueUtc)` index doesn't serve hash-range scans. Cosmos DB point operations route to the reminder's logical partition, while recovery and cluster-wide management use cross-partition hash-range queries. Capacity-plan request units for those scans and verify them with production-like reminder counts.

## Understand delivery and time buckets

Advanced Reminders persists a definition and schedules only its next occurrence. It doesn't create an in-memory timer for every registration or expand a recurring or cron schedule into all future occurrences.

The delivery lifecycle is:

1. Registration writes the reminder definition, including its next UTC due time, missed-occurrence action, and a new schedule ID.
1. The service immediately schedules one durable job for the next due time, including far-future occurrences. The job carries the target grain ID, reminder name, and schedule ID; its job ID and shard ID are written back to the reminder definition.
1. Durable Jobs partitions jobs by time and silo, allowing local scheduling and cancellation to use the existing backend's batching. Each journaled shard is a log driving its job state machine. Durable Jobs owns shard discovery, execution, and retries.
1. When the job runs, a dispatcher re-reads the reminder definition from its provider and compares the job's schedule ID with the current schedule ID. A job left behind by an update or failed cancellation can't fire the replacement schedule.
1. After the callback, the dispatcher re-reads the definition again. This prevents a callback which updates or unregisters itself from being overwritten by completion of the old occurrence.
1. For an interval or cron reminder, the service calculates and persists one next occurrence. It immediately schedules the replacement through Durable Jobs. For a one-shot reminder, there is no next occurrence, so the definition is removed.

The reminder table remains the source of truth for schedules and administrative state. Reconciliation reads definitions in pages of at most 256 rows and advances through 256 of 4,096 hash ranges per cycle. It repairs incomplete registrations whose durable job handles were not persisted. A persisted handle remains authoritative while Durable Jobs executes or retries the occurrence.

Capacity-plan both stores: the reminder provider retains definitions, and Durable Jobs retains the next scheduled occurrence for each active registration. Measure callback duration, storage throughput, due-time concentration, recovery scans, and rolling restart behavior under representative load. Await registrations and bound producer concurrency during imports.

### Understand concurrency and contention

A dispatcher grain serializes reminder mutations for its target grain. Each registration has a stable registration identity and a separate occurrence token. Replacing a schedule rotates its identity before scheduling a new job; stale jobs are ignored when their metadata no longer matches the persisted definition. Cancellation uses the existing Durable Jobs API.

Durable job handlers enter that same dispatcher request queue before reading or changing a definition. This matters for immediately due jobs: delivery waits until registration has persisted the job handle, even if Durable Jobs starts its handler before scheduling returns. Callback-initiated updates and unregister operations can reenter their own delivery call chain, and the post-callback read preserves those changes. The Durable Jobs dequeue count follows the queued request so retry limits and missed-occurrence policies retain their meaning.

Durable Jobs manages time and silo partitioning and per-shard persistence. Advanced Reminders consumes that API for scheduling and cancellation. Configure its execution and retry options for the application's workload; the reminder layer adds no ordering policy between independently due callbacks.

## Configure the silo

Use in-memory storage only for local development and tests:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="configure_in_memory_advanced_reminders":::

For Azure Storage, configure both service clients. Prefer workload identity or managed identity over account keys and connection strings:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="configure_azure_advanced_reminders":::

The example also configures a missed-occurrence grace period and a delivery-attempt safety limit. Every silo in the cluster must use the same provider and compatible options.

## Implement and register a reminder

The target grain implements <xref:Orleans.AdvancedReminders.IRemindable>. Advanced APIs include `AdvancedReminder` in their names so that classic and advanced reminder extension methods remain unambiguous in the same project.

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="advanced_reminder_grain":::

Use `GetAdvancedReminder`, `GetAdvancedReminders`, and `UnregisterAdvancedReminder` to inspect or remove registrations owned by the current grain. Store the reminder name rather than a returned handle across activations.

<xref:Orleans.AdvancedReminders.ReminderSchedule> supports these schedule forms:

| Factory | Behavior |
| --- | --- |
| `OneShot(TimeSpan)` | Run once after a relative delay, then remove the registration. |
| `OneShot(DateTime)` | Run once at an absolute UTC timestamp, then remove the registration. |
| `OneShot(DateTimeOffset)` | Normalize an offset-aware timestamp to UTC, run once, then remove the registration. |
| `Interval(TimeSpan, TimeSpan)` | Start after a relative delay and recur at a fixed period. |
| `Interval(DateTime, TimeSpan)` | Start at an absolute UTC timestamp and recur at a fixed period. |
| `Cron(string, string?)` | Recur from a five- or six-field cron expression in an optional time zone. |

Use <xref:Orleans.AdvancedReminders.ReminderCronBuilder> for common schedules or <xref:Orleans.AdvancedReminders.ReminderCronExpression.Parse*> to validate an expression. Time-zone-aware schedules persist the time-zone identifier and evaluate future occurrences using that zone, including daylight-saving transitions. Use identifiers available on every silo operating system, and verify them before deployment.

<xref:Orleans.AdvancedReminders.ReminderOptions.MinimumReminderPeriod?displayProperty=nameWithType> applies to interval and cron schedules. The default is one minute, and recurring interval periods remain strictly positive when the configured minimum is zero. An absolute `DateTime` must have `DateTimeKind.Utc`.

## Schedule one-shot work

Use <xref:Orleans.AdvancedReminders.ReminderSchedule.OneShot*> to describe work that should run once. The <xref:Orleans.AdvancedReminders.GrainReminderOneShotExtensions.RegisterOrUpdateAdvancedReminder*> convenience overloads accept the same three forms directly:

| Input | Meaning |
| --- | --- |
| `TimeSpan` | A delay measured from registration time. |
| UTC `DateTime` | An absolute UTC due time. `Local` and `Unspecified` values are rejected. |
| `DateTimeOffset` | An absolute instant, normalized to UTC using its supplied offset. |

For an absolute timestamp, prefer `DateTimeOffset`. This example registers a one-shot reminder for April 15, 2030 at 16:30 UTC:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="register_advanced_reminder_one_shot_date":::

The delay and UTC-date forms use the same registration path:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="register_advanced_reminder_one_shot_delay_and_utc":::

These overloads are available for both `Grain` and `IGrainBase`. Registry and service callers can use <xref:Orleans.AdvancedReminders.ReminderOneShotRegistrationExtensions.RegisterOrUpdateReminder*> with a grain ID. Each overload delegates to `ReminderSchedule.OneShot`, preserving the reminder name and selected missed-occurrence policy. The default policy is `Skip`; choose `FireImmediately` when an overdue one-shot should still invoke the grain.

A timestamp's offset identifies one instant. For a local calendar date, resolve the intended time zone and choose an offset explicitly when that local time is ambiguous. A one-shot schedule has a zero period, so `MinimumReminderPeriod` doesn't reject it. The due time determines when delivery becomes eligible; actual delivery can occur later because of load, retries, or recovery.

A one-shot follows the same durable path as a recurring reminder: its definition and one delivery job are persisted, and it can survive activation changes or a cluster restart when both providers are durable. At delivery:

- `FireImmediately` invokes an overdue one-shot even after the missed-reminder grace period. `Skip` removes it without invoking the grain, and `Notify` logs the miss and removes it without invoking the grain.
- After a successful callback, the reminder definition is removed automatically. Don't call `UnregisterAdvancedReminder` merely to complete a normal one-shot.
- Without `MaximumDeliveryAttempts`, a callback failure is logged and the one-shot is completed and removed. With that option configured, the same occurrence is retried by Durable Jobs and the reminder is removed when the configured dequeue-count limit is reached.
- Delivery can be retried after an uncertain distributed failure, so the callback still needs an idempotency key or another durable deduplication guard for its business effect.

Completion creates no next job. A later delivery of the completed occurrence finds no registration and returns without invoking the grain. The regression `OneShotReminder_DoesNotRepeatAfterCompletion` exercises all three time forms, advances a controlled clock 30 days after completion, and replays the old occurrence as both a normal delivery and a retry. It asserts exactly one callback, one originally scheduled job, one removal, and an absent registration. The missed-occurrence, retry, and callback-mutation suites separately verify skipped one-shots, notification-only completion, overdue delivery, failure limits, and a callback replacing its own registration.

Registering the same grain and reminder name again creates a new schedule ID and replaces the stored schedule. Orleans attempts to cancel the previous job; if cancellation is no longer possible, the old job is harmless because its schedule ID no longer matches.

## Build cron schedules

Prefer the strongly typed <xref:Orleans.AdvancedReminders.ReminderCronBuilder> factories over hand-written cron strings. The builder validates arguments and makes the intended calendar schedule visible in code:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="build_advanced_reminder_cron_schedules":::

The examples cover every minute, hourly, daily, weekdays, weekends, one weekday, the first and last day of each month, one annual date, and leap day. The factory families accept <xref:System.TimeOnly>, <xref:System.TimeSpan>, <xref:System.DateOnly>, or <xref:System.TimeZoneInfo>. A time-zone-aware cron keeps the selected local wall-clock time through daylight-saving changes. Pass the resulting builder directly to `RegisterOrUpdateAdvancedReminder`, as shown in the grain example.

Cron expressions support five fields (minute through weekday) or six fields (second through weekday). Fields accept lists, ranges, wrapping ranges, and steps. Calendar rules include last-day offsets (`L-3`), nearest weekdays (`15W`, `LW`), and ordinal weekdays (`MON#2`, `FRIL`). When both day-of-month and weekday are restricted, both must match.

### Query with offset-aware timestamps

<xref:Orleans.AdvancedReminders.ReminderCronBuilder.GetNextOccurrence*> and <xref:Orleans.AdvancedReminders.ReminderCronBuilder.GetOccurrences*> accept both UTC `DateTime` and `DateTimeOffset`. The corresponding <xref:Orleans.AdvancedReminders.ReminderCronExpression> methods accept the same inputs. `DateTimeOffset` range endpoints can use different offsets: comparisons use their UTC instants, and returned values have offset zero. An input offset identifies the starting instant; the builder's configured time zone determines the recurring calendar schedule. An expression queried directly uses UTC.

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="query_advanced_reminder_cron_offsets":::

The next-occurrence query excludes its starting instant by default. Range queries include the start and exclude the end by default; the inclusion flags can change either boundary. A reversed range throws `ArgumentException`, and a valid expression with no future calendar match returns no occurrence. Cron uses whole-second precision and Gregorian leap-year rules, including the century exceptions: 2100 has no February 29, while 2400 does.

### Seasonal clock changes

During a forward clock change, occurrences in the skipped local interval collapse into one occurrence at the first valid instant. During a backward clock change, fixed clock schedules fire once at the earlier UTC instant. Schedules containing a wildcard, range, or step in a clock field also run through the repeated interval. Returned occurrences remain strictly increasing in UTC.

Weekday ranges select the intended local calendar day even when a clock change happens inside that range. `TUE-FRI` includes Tuesday through Friday; `THU-WED` wraps across Sunday and includes all seven days. A step starts at the range's first day: `THU-WED/2` selects Thursday, Saturday, Monday, and Wednesday.

For example, consider `30 2 * * TUE-FRI` in a hypothetical zone that starts at UTC+02:00, jumps from 02:00 to 03:00 on Wednesday, March 19, 2025, then returns from 02:00 to 01:00 on Thursday. The next eligible instants are:

| Intended local day | UTC occurrence | Reason |
| --- | --- | --- |
| Tuesday, March 18 | 00:30 | Local 02:30 at UTC+02:00. |
| Wednesday, March 19 | 00:00 | Local 02:30 does not exist; catch up at local 03:00. |
| Thursday, March 20 | 00:30 | Local 02:30 occurs after the rollback. |
| Friday, March 21 | 00:30 | Local 02:30 at UTC+02:00. |
| Tuesday, March 25 | 00:30 | Saturday through Monday are outside the weekday range. |

These are scheduling instants; actual callback execution can be later because of queueing, outages, and the selected missed-occurrence policy. A fixed clock inside a repeated interval uses its earlier UTC instant once. A clock-field step such as `*/15` also selects occurrences in the repeated interval. If an operating-system transition lies between whole seconds, gap catch-up uses the first whole second at or after it. System time-zone availability and historical rules depend on the host's time-zone database; test fixtures use explicit rules to exercise transitions that are absent from a host database.


The scheduler selects matching calendar days and jumps between allowed clock values. Calendar searches cover one 400-year Gregorian cycle, so an impossible date returns no occurrence. Time-zone conversion uses the operating system's adjustment rules and keeps a bounded cache of local-date mappings.

### Common schedules

| Requirement | Builder call | Generated expression |
| --- | --- | --- |
| Every second | `EverySecond()` | `* * * * * *` |
| Every fifth second position | `EverySeconds(5)` | `*/5 * * * * *` |
| Every minute | `EveryMinute()` | `* * * * *` |
| At minute 15 of every hour | `HourlyAt(15)` | `15 * * * *` |
| At 10 minutes 30 seconds into every hour | `HourlyAt(new TimeSpan(0, 10, 30))` | `30 10 * * * *` |
| Every day at 02:30 | `DailyAt(2, 30)` | `30 2 * * *` |
| Every day at 02:30:15 | `DailyAt(2, 30, 15)` | `15 30 2 * * *` |
| Monday through Friday at 09:00 | `WeekdaysAt(9, 0)` | `0 9 * * MON-FRI` |
| Saturday and Sunday at 11:00 | `WeekendsAt(11, 0)` | `0 11 * * SAT,SUN` |
| Every selected weekday | `WeeklyOn(DayOfWeek.Monday, 10, 0)` | `0 10 * * 1` |
| Day 15 of every month | `MonthlyOn(15, 8, 0)` | `0 8 15 * *` |
| Last day of every month | `MonthlyOnLastDay(23, 30)` | `30 23 L * *` |
| March 15 every year | `YearlyOn(3, 15, 9, 0)` | `0 9 15 3 *` |
| February 29 in leap years | `YearlyOn(new DateOnly(2028, 2, 29), new TimeOnly(12, 0))` | `0 12 29 2 *` |

The hour/minute forms also have second-aware overloads. The time-of-day forms accept either `TimeOnly` or a sub-day `TimeSpan`. Add the scheduling zone with `.InTimeZone(timeZone)`, `.InTimeZone("Europe/Kyiv")`, or a factory overload whose final argument is `TimeZoneInfo`. `YearlyOn(DateOnly, ...)` uses only the month and day; the year in `DateOnly` is intentionally ignored.

For example, register a reminder for 09:00 every weekday in the selected time zone without writing a cron expression:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="register_advanced_reminder_cron":::

Advanced calendar rules also have typed helpers, so these schedules don't require hand-written expressions:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="build_advanced_reminder_cron_rules":::

| Requirement | Builder call | Generated expression |
| --- | --- | --- |
| Every fifth minute position | `EveryMinutes(5)` | `*/5 * * * *` |
| At second 15 of every minute | `EveryMinuteAtSecond(15)` | `15 * * * * *` |
| Monday, Wednesday, and Friday at 09:00 | `WeeklyOn([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], new TimeOnly(9, 0))` | `0 9 * * 1,3,5` |
| Nearest weekday to day 1 of the month at 09:00 | `MonthlyOnNearestWeekday(1, new TimeOnly(9, 0))` | `0 9 1W * *` |
| Three days before the end of every month at 09:00 | `MonthlyBeforeLastDay(3, new TimeOnly(9, 0))` | `0 9 L-3 * *` |
| Last Friday of every month at 09:00 | `MonthlyOnLast(DayOfWeek.Friday, new TimeOnly(9, 0))` | `0 9 ? * 5L` |
| Second Monday of every month at 09:00 | `MonthlyOnNth(DayOfWeek.Monday, 2, new TimeOnly(9, 0))` | `0 9 ? * 1#2` |
| Weekdays in January and March at 09:30:15 | `WeekdaysInMonthsAt([1, 3], new TimeOnly(9, 30, 15))` | `15 30 9 ? 1,3 MON-FRI` |

The helpers validate interval, second, day, occurrence, offset, and month ranges; selected days and months are de-duplicated and emitted in stable calendar order. Apply `InTimeZone(...)` when the rule represents local wall-clock time. Although second-level schedules are supported, registration still enforces `MinimumReminderPeriod`, and reminders aren't intended for high-frequency work.

### Compose every cron field

For schedules beyond the named recipes, `FromFields` exposes the complete supported field grammar without string concatenation. Its five-field overload uses minute, hour, day-of-month, month, and day-of-week. Its six-field overload adds `ReminderCronSecond` first:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="build_advanced_reminder_cron_fields":::

Each field has its own type, so an hour can't accidentally be passed as a month or day-of-week. The base operations map directly to cron syntax:

| Typed operation | Generated field | Meaning |
| --- | --- | --- |
| `Any` | `*` | Every value in the field. |
| `At(...)`, `On(...)`, or `In(...)` | `3,12` | One or more de-duplicated, sorted values. |
| `Range(start, end)` | `9-17` | Inclusive range. Reversed ranges such as `22-1`, `12-2`, or Friday-to-Monday wrap over the field boundary. |
| `Every(interval)` | `*/5` | Every fifth position starting at the field's first value. |
| `EveryFrom(start, interval)` | `10/20` | Every twentieth position from 10 through the end of the field. |
| `EveryBetween(start, end, interval)` | `5-15/5` | Stepped inclusive range, including a reversed range. |
| `Combine(...)` | `3,5-11/3,12` | A list containing ordinary values, ranges, and steps. Wildcards and special `L`, `W`, or `#` expressions can't be list members. |

The available field types and their validation boundaries are:

| Type | Values | Additional operations |
| --- | --- | --- |
| `ReminderCronSecond` | `0`–`59` | Base operations. Including this field selects six-field format. |
| `ReminderCronMinute` | `0`–`59` | Base operations. |
| `ReminderCronHour` | `0`–`23` | Base operations. |
| `ReminderCronDayOfMonth` | `1`–`31` | `NearestWeekday`, `LastDay`, `DaysBeforeLast`, `LastWeekday`, and `NearestWeekdayBeforeLast`. |
| `ReminderCronMonth` | `1`–`12` | Base operations; January is 1. |
| `ReminderCronDayOfWeek` | `DayOfWeek` | Base operations plus `Last` and `Nth`; Sunday is emitted as 0. |

The special calendar operations cover the full supported `L`, `W`, and `#` grammar:

| Requirement | Typed field | Generated field |
| --- | --- | --- |
| Weekday nearest to day 15 | `ReminderCronDayOfMonth.NearestWeekday(15)` | `15W` |
| Last calendar day | `ReminderCronDayOfMonth.LastDay` | `L` |
| Three days before month end | `ReminderCronDayOfMonth.DaysBeforeLast(3)` | `L-3` |
| Last weekday | `ReminderCronDayOfMonth.LastWeekday` | `LW` |
| Nearest weekday to five days before month end | `ReminderCronDayOfMonth.NearestWeekdayBeforeLast(5)` | `L-5W` |
| Last Friday | `ReminderCronDayOfWeek.Last(DayOfWeek.Friday)` | `5L` |
| Second Monday | `ReminderCronDayOfWeek.Nth(DayOfWeek.Monday, 2)` | `1#2` |

When both day-of-month and day-of-week are constrained, **both must match**. For example, day 13 plus Friday represents Friday the 13th. This differs from traditional Vixie crontab, which treats those two fields as an OR for compatibility.

Steps must be positive and no greater than the number of distinct field values: 60 for seconds/minutes, 24 for hours, 31 for days, 12 for months, and 7 for weekdays. Sunday aliases `0` and `7` identify the same day.

Steps operate on positions inside one calendar field; they aren't elapsed-time intervals. For example, `*/7` in the minute field selects minutes 0, 7, …, 56 and then resets at the next hour, leaving a four-minute boundary gap. Similarly, stepped days reset each month and stepped months reset each year. Use `ReminderSchedule.Interval` with an absolute UTC anchor when the requirement is a fixed elapsed cadence.

### Use raw cron strings

`ReminderCronBuilder.FromExpression(...)` remains available for configuration-provided expressions and direct interoperability. Call `Build()` or `ToCronExpression()` during startup to validate the value before registering production work:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="build_advanced_reminder_raw_cron":::

Raw expressions accept five fields or six fields with seconds:

| Position | Field | Values and names |
| --- | --- | --- |
| 1 in six-field form | Second | `0`–`59` |
| 1 or 2 | Minute | `0`–`59` |
| 2 or 3 | Hour | `0`–`23` |
| 3 or 4 | Day of month | `1`–`31` |
| 4 or 5 | Month | `1`–`12` or `JAN`–`DEC` |
| 5 or 6 | Day of week | `0`–`7` or `SUN`–`SAT`; both 0 and 7 are Sunday |

All names and macros are case-insensitive. `*` means any value, `?` is an alias for `*`, comma creates a list, hyphen creates a range, and `/` creates a step. Ranges may wrap over their field boundary. Day-of-month additionally accepts `L`, `L-n`, `W`, `LW`, and `L-nW`; day-of-week accepts `L` and `#n`.

| Raw expression | Meaning |
| --- | --- |
| `*/5 * * * *` | Every fifth minute position. |
| `0 3,5-11/3,12 1 * * *` | At second 0 of minutes 3, 5, 8, 11, and 12 during hour 1. |
| `0 9 * JAN,MAR MON-FRI` | At 09:00 on weekdays in January and March. |
| `0 9 LW * *` | At 09:00 on the last weekday of every month. |
| `0 9 L-5W * *` | At 09:00 on the weekday nearest to five days before month end. |
| `0 9 13 * FRI` | At 09:00 only when the 13th is a Friday. |
| `0 9 * * FRI-MON` | At 09:00 Friday through Monday using a reversed range. |

Supported macros are `@every_second`, `@every_minute`, `@hourly`, `@daily`, `@midnight`, `@weekly`, `@monthly`, `@yearly`, and `@annually`. A seventh year field, full month or weekday names, Quartz-specific syntax, and the `H` jitter token aren't supported. Use an explicit value or deterministic application-level load spreading instead of `H`.

Cron calendar fields don't have a stable epoch for “every other Monday”, don't expose ISO week parity, and can't select only even years. Don't approximate those schedules with day-of-month steps because month and year boundaries change the sequence. For a fixed elapsed 14-day cadence, use an absolute first occurrence and an interval:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="build_advanced_reminder_biweekly_schedule":::

That interval is anchored in UTC, so its local clock time can move across daylight-saving transitions. If the requirement is instead “09:00 local time on qualifying calendar weeks or years”, calculate the next qualifying local occurrence in application code, convert it to UTC, register a one-shot reminder, and register the following occurrence from the callback.

## Register reminders with attributes

Use <xref:Orleans.AdvancedReminders.RegisterReminderAttribute> when every instance of a grain type needs the same fixed interval or UTC cron registration. The attribute can be applied more than once to the concrete grain class:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="register_advanced_reminders_with_attributes":::

Attribute registration is lazy and applies to a specific grain identity, not to every possible key of the grain type at silo startup. When a client call, reminder delivery, or another Orleans operation activates a grain identity, Orleans runs an activation-stage hook which:

1. Builds each declared interval or cron schedule.
1. Computes a declaration identity from the schedule and missed-occurrence action and compares it with the persisted registration.
1. Creates a missing reminder, replaces a changed declaration, or leaves an unchanged registration on its current occurrence. If an unchanged row is present but its durable-job handle was not persisted, reconciliation keeps the same due time but rotates the occurrence token before creating the replacement job.

One activation which reaches this hook is therefore enough to persist the declaration. A changed attribute takes effect without a separate migration after the grain identity next activates. The replacement receives a new schedule identity and supersedes the previous durable delivery job; an old job which couldn't be canceled is harmless because delivery validates that identity.

An unchanged attribute is deliberately idempotent. Suppose an interval attribute has a 30-minute initial delay and a 30-minute period. If the grain first activates at 11:00 UTC, the first occurrence is stored for 11:30 UTC. If the grain deactivates and activates again at 11:20 UTC, reconciliation verifies the existing registration and leaves that 11:30 occurrence, schedule identity, and durable job unchanged. It does **not** reinterpret the initial delay as 30 minutes from every activation and move the occurrence to 11:50 UTC.

Removing an attribute doesn't delete its previously stored registration because the removed declaration no longer has an activation hook to identify what should be deleted. Unregister it explicitly during deployment or through the management API. If a declared reminder is deleted while the grain remains active, the attribute isn't reprocessed immediately; it is created again on a later activation.

Attribute registration has these boundaries:

- The concrete class must implement <xref:Orleans.AdvancedReminders.IRemindable>, and the Advanced Reminders service must be configured. Attributes aren't inherited by derived grain classes.
- The interval constructor accepts compile-time constant seconds for the initial delay and period. The initial delay is applied only when the reminder is missing or the attribute declaration changes, not on every activation. The cron constructor accepts a compile-time constant expression and evaluates it in UTC.
- Attributes don't represent one-shot schedules, absolute UTC due times, or time-zone-aware builders. Use `RegisterOrUpdateAdvancedReminder` and <xref:Orleans.AdvancedReminders.ReminderCronBuilder> for those cases.
- The service validates the schedule, including <xref:Orleans.AdvancedReminders.ReminderOptions.MinimumReminderPeriod?displayProperty=nameWithType>, during activation. A registration error fails that activation instead of silently creating a different schedule.

Use attributes when the declaration should be reconciled on every activation. Explicit registration is preferable when schedules come from configuration or tenant data, require a time-zone-aware builder, or must be changed while the grain remains active.

## Select missed-occurrence behavior

An occurrence is missed when it is older than <xref:Orleans.AdvancedReminders.ReminderOptions.MissedReminderGracePeriod?displayProperty=nameWithType> when its durable job is processed.

The grace period is configured globally for the Advanced Reminders service. Select the action for each reminder during explicit registration or with the `action` argument of <xref:Orleans.AdvancedReminders.RegisterReminderAttribute>; the default is `Skip`.

| <xref:Orleans.AdvancedReminders.Runtime.MissedReminderAction> | Behavior |
| --- | --- |
| `Skip` | Skip the callback; advance a recurring schedule or remove a one-shot. |
| `FireImmediately` | Invoke when processed; advance a recurring schedule or complete a one-shot. It delivers one occurrence rather than replaying every missed interval. |
| `Notify` | Log a warning and skip the callback; advance a recurring schedule or remove a one-shot. |

Reminder delivery can occur after its nominal due time and can be retried after uncertain distributed failures. Make callback effects idempotent. By default, callback exceptions are logged and a recurring series advances to its next occurrence. When `MaximumDeliveryAttempts` is set, the same occurrence uses the Durable Jobs retry policy up to that dequeue-count limit and the registration is removed at the limit. The Durable Jobs policy must permit at least as many attempts.

### Read missed notifications

`Notify` emits a structured warning through the silo's logging pipeline. The logger category is `Orleans.AdvancedReminders.Runtime.ReminderService.AdvancedReminderService`; the message contains `missed due window`, with fields `ReminderName`, `GrainId`, `Due`, and `Now`. `Due` is the missed UTC due time and `Now` is the UTC processing time. Enable that category at `Warning` or lower and retain the structured fields in your logging backend.

For a local silo, JSON console logging makes the records available to the console and any collector reading its output:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="configure_advanced_reminder_missed_logging":::

Use the same logging configuration with your production hosting and storage setup. Filter retained records by category, message, and reminder or grain identity to diagnose missed work or create alerts. Notification is a log record: it doesn't call `ReceiveReminder` or expose a separate subscription API or persistent event stream.

To inspect reminders which are **currently missed**, page a management query:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="query_advanced_reminder_missed":::

<xref:Orleans.AdvancedReminders.ReminderQueryFilter.MissedBy> is the query's threshold, independent of the service's delivery grace period. A match has a due time overdue by at least that threshold and an absent last-fire time or one earlier than its due time. This query reads current registrations, not historical notifications. When a recurring schedule advances, its old missed occurrence disappears from the query; a skipped or notified one-shot is removed altogether. Use retained logs for those past notifications.

## Query and repair registrations

Get the singleton <xref:Orleans.AdvancedReminders.IReminderManagementGrain> through `GetReminderManagementGrain()`. Page results instead of materializing the whole reminder table:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="query_advanced_reminders":::

Continuation tokens are opaque and belong to the query which produced them. Paging order is stable storage-hash-bucket order, then immutable grain ID and reminder name inside each bucket. Due-time filters are evaluated against current row values without making mutable due timestamps part of the cursor. The Dashboard deliberately does not run a separate exact-count query for every page because that would read the entire reminder table. Its advanced-reminder counter therefore reports `Paged`; follow continuation tokens until they end when an exact count is operationally necessary.

The management grain consumes provider continuation pages of at most 256 rows and retains only the best `pageSize + 1` candidates needed for the public response. A skewed bucket therefore increases provider reads without increasing retained reminder memory. A subsequent public continuation page rescans that bucket after the management cursor so ordering remains stable without caching the full bucket. Capacity-plan administrative scans against provider throughput and use narrow filters when possible.

Management APIs can list all, overdue, due-range, per-grain, or filtered registrations; update missed-occurrence action; repair next-due state; and delete selected registrations. Use `ListDueInRangeAsync` with the current UTC time and a future upper bound for an upcoming-reminder query. The former unpaged `UpcomingAsync` shape is intentionally absent because an `IEnumerable` result would require materializing every match. A grain-type filter is evaluated during storage paging but still scans storage because providers don't index reminder definitions by grain type.

## Configure cleanup guardrails

Two independent cleanup options are disabled by default:

- `DeleteReminderWhenGrainTypeIsUnavailable` removes a due reminder only when a stable cluster membership has a complete manifest for every active silo and none declares the target grain type. It fails closed during incomplete membership or manifest state, but a type can still be intentionally absent during deployment. Enable it only with a deployment process which makes that trade-off safe.
- `MaximumDeliveryAttempts` removes a repeatedly failing reminder at the configured Durable Jobs dequeue count. This is a resource-safety limit, not an exact callback-exception counter.

Configure the grace threshold and cleanup guards deliberately. The grace period decides when an occurrence becomes missed; the per-registration `MissedReminderAction` shown above decides what happens next:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="configure_advanced_reminder_cleanup":::

The recovery service divides reminder storage into 4,096 hash ranges and advances through 256 ranges per one-minute reconciliation cycle. Each provider returns continuation pages of at most 256 rows, and incomplete registrations are repaired in batches of 32. Missing handles are repaired regardless of due time. Persisted job handles remain authoritative while Durable Jobs executes or retries an occurrence. The caller observes registration failures, and the singleton reconciliation cursor repairs persisted rows with missing handles. A forced repair rotates the occurrence token before replacing the job, so an older job becomes a harmless no-op if it later runs.

Deletion occurs only through a defined policy or an explicit administrative action:

| Condition | Default behavior | Opt-in or explicit behavior |
| --- | --- | --- |
| Missing job handle | Reconcile and schedule the current occurrence again. | Force repair rotates the occurrence token before scheduling a replacement. |
| Callback throws | Log and advance a recurring series; a one-shot completes. | Set `MaximumDeliveryAttempts` to retry the same occurrence and delete the registration at the limit. |
| Target grain type isn't declared | Keep the registration. | `DeleteReminderWhenGrainTypeIsUnavailable` deletes it when it becomes due, but only after stable membership and complete active-silo manifests prove absence. |
| Known retired or irreparable registration | Keep the registration. | Page a narrow management query, inspect each match, then call `DeleteAsync`. |

For explicit retirement, query one grain type through <xref:Orleans.AdvancedReminders.ReminderQueryFilter>, inspect each page, and delete only confirmed registrations:

:::code language="csharp" source="../snippets/compiled/Grains/AdvancedSchedulingSnippets.cs" id="repair_or_delete_advanced_reminders":::

Use `RepairAsync` when the definition is valid but its next due state needs recalculation. Use `DeleteAsync` only after the application has established that the grain type or registration is retired or irreparable. Both methods use the reminder service and concurrency checks; don't delete provider rows directly. Prefer this bounded administrative workflow to an unfiltered cluster-wide scan.

## Operate the service

- Persist both reminder definitions and Durable Jobs data in production, and include both in backup and disaster-recovery plans.
- Keep storage credentials out of source and grant only the table, container, database, or key permissions required by the provider.
- Alert on persistent scheduling failures, overdue reminders, repeated callback failures, and Durable Jobs shard recovery or quarantine events.
- Add the `Microsoft.Orleans.DurableJobs` activity source to tracing. Scheduled jobs preserve W3C trace context when one is available at registration.
- Validate rolling upgrades against the same provider data. Don't run versions with incompatible reminder or job serialization contracts against one store.

For package names, see [Orleans NuGet packages](../resources/nuget-packages.md#reminders-and-durable-jobs).
