# Microsoft Orleans Advanced Reminders for ADO.NET

Persist Advanced Reminders definitions in SQL Server, PostgreSQL, MySQL/MariaDB, or Oracle. The same grain API supports recurring intervals, one-shot deadlines, five- or six-field cron schedules with time zones, activation-time attributes, missed-occurrence policies, and paged management.

This prerelease provider stores **definitions**. Configure a Durable Jobs backend separately for delivery. Both stores must be durable in production. Classic reminder definitions use a separate schema and aren't imported automatically.

## Install and prepare the database

```shell
dotnet add package Microsoft.Orleans.AdvancedReminders.AdoNet
# SQL Server driver for the example below:
dotnet add package Microsoft.Data.SqlClient
```

Apply the corresponding advanced-reminder SQL script from this package's source directory before starting the silo:

| Database | Invariant | Schema script |
| --- | --- | --- |
| SQL Server | `Microsoft.Data.SqlClient` | `SQLServer-Reminders-Advanced.sql` |
| PostgreSQL | `Npgsql` | `PostgreSQL-Reminders-Advanced.sql` |
| MySQL/MariaDB | `MySql.Data.MySqlClient` | `MySQL-Reminders-Advanced.sql` |
| Oracle | `Oracle.DataAccess.Client` | `Oracle-Reminders-Advanced.sql` |

Install the matching driver (`Npgsql`, `MySql.Data`, or the Oracle provider for that invariant) when selecting another database. The SQL script alone doesn't install a database driver. The `(ServiceId, GrainHash)` index supports bounded recovery and management scans; preserve it when upgrading an existing prerelease schema.

See the [ADO.NET database setup guide](https://learn.microsoft.com/dotnet/orleans/host/configuration-guide/adonet-configuration) for connection and provider setup.

## Configure a development silo

Supply `ConnectionStrings:Orleans` through configuration, for example the `ConnectionStrings__Orleans` environment variable. This example keeps delivery jobs in memory for local development:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Orleans")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Orleans.");

builder.UseOrleans(silo => silo
    .UseLocalhostClustering()
    .Configure<ClusterOptions>(options =>
    {
        options.ServiceId = "advanced-reminder-sample";
        options.ClusterId = "development";
    })
    .UseInMemoryDurableJobs()
    .UseAdoNetAdvancedReminderService(options =>
    {
        options.Invariant = "Microsoft.Data.SqlClient";
        options.ConnectionString = connectionString;
    }));

await builder.Build().RunAsync();
```

For persisted delivery, replace `UseInMemoryDurableJobs()` with a durable backend. For example, install `Microsoft.Orleans.DurableJobs.AzureStorage` and `Azure.Identity`, then use this configuration in your silo setup:

```csharp
using Azure.Identity;
using Azure.Storage.Blobs;
using Orleans.Hosting;

// Opt in to the existing experimental journal storage options.
#pragma warning disable ORLEANSEXP005
public static class PersistentReminderStorage
{
    public static ISiloBuilder Configure(
        ISiloBuilder silo, string connectionString, Uri blobServiceUri)
        => silo
            .UseAzureBlobDurableJobs(options =>
            {
                options.BlobServiceClient = new BlobServiceClient(
                    blobServiceUri, new DefaultAzureCredential());
                options.ContainerName = "advanced-reminder-jobs";
            })
            .UseAdoNetAdvancedReminderService(options =>
            {
                options.Invariant = "Microsoft.Data.SqlClient";
                options.ConnectionString = connectionString;
            });
}
#pragma warning restore ORLEANSEXP005
```

Configure clustering and grain-state storage separately. Use the same service identity and compatible provider options on every silo. Include the definition database and delivery store in recovery planning.

## Register cron, intervals, and one-shots in a grain

This complete grain exposes a five-minute poll, a Tuesday-through-Friday report in Kyiv time, a month-end report in Dubai time, and an offset-aware one-shot deadline:

```csharp
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

public interface IReportGrain : IGrainWithStringKey
{
    Task StartAsync();
    Task ScheduleDeadlineAsync(DateTimeOffset deadline);
    Task StopAsync(string name);
}

public sealed class ReportGrain : Grain, IReportGrain,
    Orleans.AdvancedReminders.IRemindable
{
    public async Task StartAsync()
    {
        await this.RegisterOrUpdateAdvancedReminder(
            "poll", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

        await this.RegisterOrUpdateAdvancedReminder(
            "weekday-report",
            ReminderCronBuilder.FromExpression("30 9 * * TUE-FRI")
                .InTimeZone("Europe/Kyiv"),
            MissedReminderAction.FireImmediately);

        await this.RegisterOrUpdateAdvancedReminder(
            "month-end",
            ReminderCronBuilder.MonthlyOnLastDay(new TimeOnly(23, 30))
                .InTimeZone("Asia/Dubai"),
            MissedReminderAction.Notify);
    }

    public Task ScheduleDeadlineAsync(DateTimeOffset deadline)
        => this.RegisterOrUpdateAdvancedReminder(
            "deadline", deadline, MissedReminderAction.FireImmediately);

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
        // Dispatch by name to idempotent application work.
        Console.WriteLine($"{reminderName}: delivered at {status.CurrentTickTime:O}");
        return Task.CompletedTask;
    }
}
```

Activate a grain through a client and call `StartAsync()` to persist these programmatic registrations. Calling it again updates the same names. Different names are independent. The initial delay may be shorter than the default one-minute minimum recurring period.

### More cron calendars

```csharp
using Orleans.AdvancedReminders;

var parisMorning = ReminderCronBuilder.DailyAt(9, 30)
    .InTimeZone("Europe/Paris");
var viennaWorkday = ReminderCronBuilder.WeekdaysAt(new TimeOnly(9, 0))
    .InTimeZone("Europe/Vienna");
var sydneyLastFriday = ReminderCronBuilder.MonthlyOnLast(
    DayOfWeek.Friday, new TimeOnly(17, 0)).InTimeZone("Australia/Sydney");
var leapDay = ReminderCronBuilder.FromExpression("0 12 29 2 *")
    .InTimeZone("Asia/Dubai");
var wrappedWeek = ReminderCronBuilder.FromExpression("30 9 * * THU-WED/2")
    .InTimeZone("Australia/Lord_Howe");

var from = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.FromHours(4));
DateTimeOffset? next = wrappedWeek.GetNextOccurrence(from);
var occurrences = wrappedWeek.GetOccurrences(from, from.AddDays(7)).ToArray();
```

`THU-WED/2` selects Thursday, Saturday, Monday, and Wednesday. Five fields start with minutes; a sixth leading field specifies seconds. Day-of-month and weekday restrictions are combined with AND. Steps reset within each field; use intervals for elapsed durations. Time-zone IDs must exist on every silo.

During a forward clock change, skipped local occurrences collapse to the first valid whole second. A fixed clock schedule runs once at the earlier UTC instant during rollback; wildcard/range/step clock fields also traverse the repeated interval. A weekday range alone doesn't repeat a fixed clock time. Queries return UTC instants; actual callbacks can run later and follow the selected missed policy.

### One-shot time forms

The following methods schedule the same named deadline using different input forms:

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

The grain must implement the advanced `IRemindable` interface. `DateTime` requires UTC; `DateTimeOffset` is normalized to UTC. Explicit `ReminderSchedule.OneShot(...)` factories accept the same three forms. On completion, the definition is removed and no next job is scheduled. A later stale delivery finds no registration and does nothing. One-shot means one intended occurrence; uncertain distributed failures can still cause repeated attempts, so application effects must be idempotent.

## Register automatically with attributes

Declare interval and UTC cron registrations directly on the concrete grain class:

```csharp
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

public interface IAutomaticReportGrain : IGrainWithStringKey { }

[RegisterReminder("poll", dueSeconds: 10, periodSeconds: 300)]
[RegisterReminder("weekday-report", "30 9 * * TUE-FRI",
    action: MissedReminderAction.FireImmediately)]
[RegisterReminder("month-end", "0 23 L * *",
    action: MissedReminderAction.Notify)]
public sealed class AutomaticReportGrain : Grain, IAutomaticReportGrain,
    Orleans.AdvancedReminders.IRemindable
{
    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // Execute the poll, weekday report, or month-end work idempotently.
        Console.WriteLine($"{reminderName}: {status.CurrentTickTime:O}");
        return Task.CompletedTask;
    }
}
```

Attributes are applied when a grain identity activates. An unchanged declaration preserves its pending occurrence; a changed or missing declaration is registered again. Removing an attribute doesn't delete its stored reminder: unregister it explicitly. Attributes aren't inherited. Attribute cron is **UTC**, and interval arguments are seconds with a positive period. Use programmatic registration for time zones, one-shots, absolute timestamps, or tenant-specific schedules.

## Missed notifications, failures, and management

An occurrence is missed when its lateness exceeds `ReminderOptions.MissedReminderGracePeriod` (default 30 seconds). Choose a policy per registration:

| Action | Outcome |
| --- | --- |
| `Skip` (default) | Skip the callback; advance a recurring schedule or remove a one-shot. |
| `FireImmediately` | Deliver the missed occurrence once when processed; recurring schedules then advance without replaying every missed interval. |
| `Notify` | Write a warning and skip the callback; advance a recurring schedule or remove a one-shot. |

Read `Notify` through the logging provider configured on the silo. The category is `Orleans.AdvancedReminders.Runtime.ReminderService.AdvancedReminderService`; the warning contains `ReminderName`, `GrainId`, `Due`, and `Now`. Enable that category at `Warning` or lower and retain structured logs for historical alerts. There is no separate notification callback or retained missed-event stream.

Management's `ReminderQueryStatus.Missed` filter reads **currently stored registrations**, not notification history. After a missed one-shot is removed or a recurring reminder advances, the old missed occurrence is no longer available through that query. `MissedBy` is the query's threshold, independent of the service grace period.

Use `GetReminderManagementGrain()` with `ListFilteredAsync` and opaque continuation tokens to page current registrations. Filters include status, missed action, grain type, schedule kind, and due bounds. `ListForGrainAsync`, `SetActionAsync`, `RepairAsync`, and `DeleteAsync` support scoped administration; the Dashboard exposes these operations too. Hash-bucket paging bounds retained memory but can still scan the selected service's rows; it isn't globally sorted by due time or a snapshot during concurrent changes.

By default, callback exceptions are logged and the schedule advances; a one-shot is removed. Set `MaximumDeliveryAttempts` to retry through Durable Jobs and remove the registration at that dequeue-count limit. Configure a compatible retry policy on the job backend. `DeleteReminderWhenGrainTypeIsUnavailable` is disabled by default and requires stable, complete cluster manifests before deleting a due reminder for an absent grain type. Recovery repairs incomplete job handles through bounded database pages. Callback-initiated updates or removal survive completion of the old occurrence.

For the full API, cron grammar, options, management examples, and delivery semantics, see the [Advanced Reminders package guide](../../Orleans.AdvancedReminders/README.md) and [Advanced Reminders documentation](../../../docs/site/src/content/docs/grains/advanced-reminders.md).
