---
title: Implement long-running reminders
description: Run reminder-triggered work for longer than a grain call timeout by continuing it in the background.
ms.date: 08/21/2026
ms.topic: how-to
---

# Implement long-running reminders

Orleans delivers a reminder by calling <xref:Orleans.IRemindable.ReceiveReminder*> as a grain call. Keeping that call incomplete for longer than the configured response timeout can cause the reminder delivery to time out.

For long-running reminder work, use the callback to start one cooperative background loop and return promptly. The loop continues on the grain scheduler and yields between bounded units of work. If the loop stops or the activation is replaced, a later reminder tick starts it again.

Use this recipe when one grain identity owns restartable work which should continue between reminder ticks. Model each iteration as a bounded, asynchronous operation and persist enough progress to resume after activation or process loss.

> [!IMPORTANT]
> The reminder definition is durable, while the background task belongs to the current activation. Reminder ticks can be missed or delivered more than once during cluster changes. Make each unit of work idempotent and reconcile it from durable business state.

## Prerequisites

- Configure a [reminder provider](../grains/reminders.md#configure-reminder-storage) on every silo.
- Choose a reminder period at or above <xref:Orleans.Hosting.ReminderOptions.MinimumReminderPeriod?displayProperty=nameWithType> and shorter than the grain's [activation collection age](../host/configuration-guide/activation-collection.md).
- Design the work loop so that every iteration accepts cancellation, yields asynchronously, and records durable progress.

## Schedule a frequent reminder

Reminder ticks count as activation activity. A reminder period shorter than the activation collection age keeps the grain active under normal idle collection, allowing the background task to continue making progress. If the activation ends because of shutdown, failure, migration, or explicit deactivation, a later tick activates the grain again and starts a new worker.

Register the reminder with a short period:

:::code language="csharp" source="../snippets/compiled/HowTo/LongRunningReminderSnippets.cs" id="schedule_long_running_reminder":::

This example uses the default minimum reminder period of one minute, which is shorter than the default activation collection age of 15 minutes. If the application configures either value differently, keep the reminder period within the supported minimum and below the collection age.

The reminder period controls activation activity and how quickly a stopped worker restarts. The background loop controls how frequently work is processed.

## Implement the worker

The following grain registers a reminder, starts at most one worker for each activation, observes worker failures, and stops the worker during deactivation:

:::code language="csharp" source="../snippets/compiled/HowTo/LongRunningReminderSnippets.cs" id="long_running_reminder_grain":::

The implementation relies on these behaviors:

1. <xref:Orleans.IRemindable.ReceiveReminder*> executes as a grain request. Returning <xref:System.Threading.Tasks.Task.CompletedTask?displayProperty=nameWithType> completes that reminder delivery without waiting for the background loop.
1. <xref:System.Threading.Tasks.ConfigureAwaitOptions> controls how the first await resumes. `ForceYielding` ensures that the loop starts after `ReceiveReminder` returns, and `ContinueOnCapturedContext` resumes it on the grain scheduler, where it can safely access grain state.
1. `_backgroundTask` limits the activation to one worker. If the worker completes after a failure, the next reminder tick starts a new worker.
1. <xref:Orleans.Grain.OnDeactivateAsync*> cancels the worker and waits within the runtime's deactivation deadline. Abrupt process termination can skip this callback, so durable progress remains the recovery source.

Replace `ProcessNextBatch` with one bounded unit of application work. Each iteration should await I/O or otherwise yield so that other queued work can run. Since other grain turns can execute while the loop is awaiting, recheck any grain state whose value affects the next operation.

Call `Start` once through a grain reference. <xref:Orleans.GrainReminderExtensions.RegisterOrUpdateReminder*> creates the durable schedule; calling `Start` again updates the same named reminder.

## Handle failures and recovery

The worker observes and logs unexpected exceptions because `ReceiveReminder` returns <xref:System.Threading.Tasks.Task.CompletedTask?displayProperty=nameWithType> to the reminder runtime. Completing the failed worker lets the next reminder tick restart it.

Persist a checkpoint before advancing to the next unit of work. A typical iteration:

1. Reads the next incomplete item from durable state.
1. Performs an idempotent side effect using a stable operation identifier.
1. Records completion durably.
1. Continues with the next item.

This sequence lets a new activation reconcile an interrupted operation after collection, migration, silo shutdown, or process failure.

## Verify the recipe

1. Call `Start` and confirm that the first reminder tick starts one worker.
1. Let another reminder tick arrive while the worker is active and confirm that one worker remains active.
1. Deactivate or restart the hosting silo and confirm that a later reminder tick creates a new activation and resumes from the durable checkpoint.
1. Make one work iteration fail and confirm that the exception is logged and the next reminder tick restarts the worker.

For periodic callbacks which run only while the grain is active, use a [grain timer](../grains/timers.md). For work which completes within one reminder invocation, return the work task directly from `ReceiveReminder`.
