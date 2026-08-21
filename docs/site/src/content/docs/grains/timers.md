---
title: Grain timers
description: Schedule activation-scoped periodic work as Orleans grain turns.
ms.date: 08/21/2026
ms.topic: concept-article
---

# Grain timers

A grain timer schedules periodic work for one grain activation. Each callback executes as a grain turn on that activation and follows Orleans request scheduling, tracing, and grain call filter behavior. The runtime owns the timer for the activation lifetime and discards it when the activation deactivates.

Use a grain timer for frequent work whose lifetime and state belong to the current activation. Use a [reminder](reminders.md) when the schedule belongs to the logical grain and must survive activation or cluster lifecycle changes.

## Register a grain timer

Register timers with <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*>. <xref:Orleans.Grain.RegisterTimer*> is obsolete.

:::code language="csharp" source="../snippets/compiled/Grains/WorkersAndTimersSnippets.cs" id="grain_timer":::

<xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> returns <xref:Orleans.Runtime.IGrainTimer>. Keep the handle when the activation needs to change or stop the schedule.

## Timer behavior

<xref:Orleans.Runtime.GrainTimerCreationOptions> controls the initial schedule and callback behavior:

| Property | Default | Runtime behavior |
|---|---:|---|
| <xref:Orleans.Runtime.GrainTimerCreationOptions.DueTime> | Required | Delays the first callback. <xref:System.TimeSpan.Zero> schedules it immediately, and <xref:System.Threading.Timeout.InfiniteTimeSpan> leaves the timer paused. |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.Period> | Required | Delays the next callback after the current callback completes. <xref:System.Threading.Timeout.InfiniteTimeSpan> creates a single scheduled callback. |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave> | `false` | Applies the grain's normal reentrancy rules. `true` allows the callback to interleave with other grain calls and timers. |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> | `false` | Controls whether each callback extends the activation's idle lifetime. |

### Callback scheduling

A timer callback never overlaps itself. Orleans waits for the callback task to complete and then measures the period before scheduling the next callback. Callback duration therefore adds to the interval between callback starts.

Timer callbacks are local-only messages addressed to their activation. They participate in normal turn scheduling and stay on the activation which registered them.

### Interleaving

With <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave> set to `false`, the callback follows the grain's reentrancy configuration like a grain method call. A non-reentrant grain processes the callback without interleaving it with other requests. A reentrant grain can interleave the callback according to its scheduling rules.

Set <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave> to `true` when the callback can safely observe grain state changing across awaits while other turns execute.

### Activation lifetime

Timer callbacks leave the activation's idle lifetime unchanged by default. Orleans can collect an otherwise idle activation, which ends all timers owned by that activation.

With <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> set to `true`, each callback extends the activation lifetime. A period shorter than the configured idle collection period keeps the activation active through successive callbacks. Infrequent callbacks still allow collection between ticks.

## Change or stop a timer

Call <xref:Orleans.Runtime.IGrainTimer.Change*> to replace the due time and period. The new due time schedules the next callback, and the new period applies after that callback completes. A change made inside a running callback takes effect after the callback completes.

Dispose <xref:Orleans.Runtime.IGrainTimer> to cancel its callback token and stop future callbacks. Orleans also cancels the token and disposes the timer when the activation begins deactivating.

## Handle callback failures

Orleans logs exceptions returned by a timer callback and schedules the next callback after the configured period. Keep application state consistent before allowing an exception to escape, and use grain state or another durable store when recovery must span activation failure.

The callback's <xref:System.Threading.CancellationToken> signals timer disposal and activation shutdown. Observe it in asynchronous work so deactivation can complete promptly.

## POCO grains

Grains implementing <xref:Orleans.IGrainBase> directly use <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*>. Inject <xref:Orleans.Timers.ITimerRegistry> when infrastructure code needs lower-level registration through the current <xref:Orleans.Runtime.IGrainContext>.

See [POCO grains](../migration-guide.md#poco-grains-and-igrainbase) for the interface-only grain model.

## Troubleshoot grain timers

| Observed behavior | Runtime behavior and action |
|---|---|
| The timer ends after activation collection or silo failure. | The timer belongs to that activation. Register it during activation setup, or use a [reminder](reminders.md) for a schedule which survives activation changes. |
| Callback starts drift later than wall-clock intervals. | Orleans measures the period after callback completion. Shorten the callback, adjust the period, or model wall-clock scheduling with durable application state. |
| Other grain calls execute while the callback awaits. | The grain is reentrant or the timer enables <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave>. Protect invariants across await points or use non-interleaved scheduling. |
| An idle activation remains active. | <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> extends its lifetime on each callback. Set it to `false` when collection should follow the normal idle timeout. |
| A callback exception appears repeatedly. | Orleans logs the exception and continues the schedule. Make the callback converge from durable state, then resolve the underlying failure. |
