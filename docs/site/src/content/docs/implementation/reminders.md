---
title: Reminder implementation
description: Explain reminder ownership, durable table reconciliation, scheduling, and delivery behavior inside Orleans.
ms.date: 08/21/2026
ms.topic: concept-article
---

# Reminder implementation

Reminders are durable schedules with volatile local execution. A reminder row is stored in an `IReminderTable`; the silo responsible for the grain's consistent-ring range loads that row into `LocalReminderService` and runs the schedule locally. The table is the source of truth for registration, while the local `ReminderData` state is a cache and timer.

## Registration and ownership

`ReminderRegistry` validates names, due times, periods, and the configured minimum period before forwarding the operation to the grain service for the grain's current owner. `LocalReminderService` performs a responsibility check, upserts the row, and reconciles its local entry using the returned ETag. Conditional removal succeeds for the matching ETag and preserves newer registrations.

The reminder service is a grain service attached to the consistent ring. Ownership changes when membership changes; a new owner reconstructs responsibility from the durable row during its next table read.

## Refresh and reconciliation

After the reminder table starts successfully, the service refreshes the ring range periodically. Reads are staggered and retried during initialization. Each refresh reads the range, compares the table sequence with local mutations, starts or updates owned entries whose next tick is within <xref:Orleans.Hosting.ReminderOptions.ReminderLoadingWindow>, and stops entries which disappeared, moved elsewhere, or no longer fall within the loading window. A local sequence prevents a concurrent registration or removal from being overwritten by an older refresh result.

The reminder table contract returns every row in the owner's consistent-hash range. A refresh therefore transfers and materializes all owned rows before applying the loading window. Resident schedules scale with reminders due within the window, while refresh I/O, deserialization, and peak allocations scale with all reminders owned by the silo.

Reminder ownership uses periodic reconciliation. A brief ownership overlap during membership convergence is possible; ETags, responsibility checks, and the service's delivery state converge the overlap toward the current owner.

## Tick delivery

Each local reminder reports one of three states: stopped, running, or tombstone. A stopped reminder keeps its run task inactive. A running reminder computes the next due time from the stored start time and period, waits using the reminder `TimeProvider`, and invokes the target grain through the normal messaging path. A tombstone records a stop reason while refresh reconciliation observes the scheduling decision. The service counts active deliveries, closes tick admission after shutdown begins, and waits for active deliveries to quiesce.

The provider durably stores the schedule, while owners generate individual ticks in memory. After owner or process failure, the next owner reconstructs the schedule and calculates the next delivery from current time. Applications reconcile timing gaps from durable business state, and idempotent callbacks tolerate duplicate execution.

## Storage and operational boundaries

The in-memory table is suitable for development only. Production tables provide the durability and concurrency behavior required for ownership changes, but their consistency and availability characteristics remain provider-specific. Table startup is part of silo lifecycle initialization; failure to open the configured table prevents normal reminder service startup.

The [reminders guide](../grains/reminders.md) covers registration and application behavior. Reminder persistence, ring movement, refresh intervals, and callback failures determine observed scheduling behavior.

Source: [`LocalReminderService`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Reminders/ReminderService/LocalReminderService.cs), [`ReminderRegistry`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Reminders/ReminderService/ReminderRegistry.cs), and [`IReminderTable`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Reminders/SystemTargetInterfaces/IReminderTable.cs).
