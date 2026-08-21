---
title: Reminders
description: Schedule durable periodic grain work which survives activation and cluster lifecycle changes.
ms.date: 08/21/2026
ms.topic: concept-article
---

# Reminders

A reminder stores a periodic schedule for one logical grain. The configured reminder provider preserves the definition, and Orleans delivers ticks through normal grain request scheduling. A tick activates the grain when it has no current activation.

Use a reminder for work whose schedule must survive activation changes and cluster restarts. Reminders suit periods measured in minutes, hours, or days. A reminder can wake a grain which then creates a [grain timer](timers.md) for finer-grained activation-scoped work.

## Receive reminder ticks

A grain receiving reminders implements <xref:Orleans.IRemindable>:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="remindable_report_grain":::

Orleans invokes <xref:Orleans.IRemindable.ReceiveReminder*> with the reminder name and <xref:Orleans.Runtime.TickStatus>. The callback executes as a grain request and follows the grain's scheduling and reentrancy rules.

## Register or update a reminder

Register a named reminder from the grain:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="register_reminder":::

<xref:Orleans.GrainReminderExtensions.RegisterOrUpdateReminder*> creates the durable definition. Registering the same name again replaces its due time and period.

The returned <xref:Orleans.Runtime.IGrainReminder> is a handle to the current registration. Persist the reminder name in application state and retrieve a current handle after activation when needed.

## Retrieve or remove a reminder

Retrieve the current handle and unregister the reminder:

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="unregister_reminder":::

Use <xref:Orleans.GrainReminderExtensions.GetReminder*> for one named reminder and <xref:Orleans.GrainReminderExtensions.GetReminders*> for all reminders registered by the grain.

## Reminder behavior

The reminder provider durably stores each definition. The responsible silo loads the definition, calculates occurrences from its persisted start time and period, and sends tick requests to the grain.

Individual tick messages are transient. Cluster unavailability or ownership movement can leave a scheduled occurrence undelivered, while the durable definition continues producing later occurrences. Ownership convergence can also produce duplicate callback execution. Process each callback idempotently and reconcile work from durable business state.

<xref:Orleans.Runtime.TickStatus.FirstTickTime> and <xref:Orleans.Runtime.TickStatus.Period> identify the theoretical schedule. <xref:Orleans.Runtime.TickStatus.CurrentTickTime> records when the runtime initiated the current delivery. Applications can compare those values with persisted progress to detect and reconcile missed occurrences.

Orleans delivers reminders to one activation of the grain identity. Delivery activates the grain when needed, including after the previous activation was collected or its silo stopped.

## Reminder timing constraints

Reminder registration accepts schedules with these boundaries:

- `dueTime` is <xref:System.TimeSpan.Zero> or greater. Zero schedules the first tick immediately.
- `dueTime` fits within the remaining <xref:System.DateTime> range from registration time.
- `period` is positive and at least <xref:Orleans.Hosting.ReminderOptions.MinimumReminderPeriod?displayProperty=nameWithType>.
- The default minimum period is one minute.

The runtime rejects negative values and <xref:System.Threading.Timeout.InfiniteTimeSpan>. It also rejects a `dueTime` which places the first tick after <xref:System.DateTime.MaxValue>.

For one scheduled execution, register a valid positive period and unregister the reminder in its first callback after the durable work succeeds.

## Configure reminder storage

Every silo configures the same reminder provider so ownership can move across the cluster. Production deployments use a durable provider such as Azure Table, ADO.NET, Redis, Amazon DynamoDB, or [Cosmos DB](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.Cosmos). The in-memory provider stores definitions for the lifetime of the cluster and supports local development and tests.

Configure each provider through the API supplied by its package. See [Configure Amazon DynamoDB reminders](reminders/dynamodb.md) for a compiled example which configures DynamoDB clustering and reminder storage independently. When composing resources with Aspire, see [Orleans and Aspire integration](../host/aspire-integration.md).

The reminder table is part of silo startup. Provider availability and consistency determine whether the service can load, update, and reconcile registrations. See [Reminder implementation](../implementation/reminders.md) for ownership, refresh, and delivery internals.

## POCO grains

Grains implementing <xref:Orleans.IGrainBase> directly use the same <xref:Orleans.GrainReminderExtensions> APIs. Inject <xref:Orleans.Timers.IReminderRegistry> when infrastructure code needs lower-level access through the current grain context.

See [POCO grains](../migration-guide.md#poco-grains-and-igrainbase) for the interface-only grain model.

## Troubleshoot reminders

| Observed behavior | Runtime behavior and action |
|---|---|
| Registration reports that the reminder service is not configured. | Configure one reminder provider on every silo before registering reminders. |
| Registration rejects the period. | Use a positive period at or above <xref:Orleans.Hosting.ReminderOptions.MinimumReminderPeriod?displayProperty=nameWithType>. |
| A tick arrives after a long cluster interruption. | Reconcile the theoretical schedule in <xref:Orleans.Runtime.TickStatus> with durable progress and process the outstanding business work. |
| The callback executes more than once for the same business interval. | Reminder ownership converges during membership changes. Use an idempotency key or durable completion marker for each interval. |
| Definitions disappear after a full cluster restart. | The in-memory provider scopes definitions to the cluster lifetime. Configure a durable production provider. |
| High-frequency work needs sub-minute intervals. | Use the reminder to activate the grain, then register a [grain timer](timers.md) for the active phase. |
