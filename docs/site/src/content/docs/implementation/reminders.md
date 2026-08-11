---
title: Reminder implementation
description: Explain reminder ownership, durable table reconciliation, scheduling, and delivery behavior inside Orleans.
ms.date: 08/11/2026
ms.topic: concept-article
---

# Reminder implementation

Reminders are durable schedules with volatile local execution. A reminder row is stored in an `IReminderTable`; the silo responsible for the grain's consistent-ring range loads that row into `LocalReminderService` and runs the schedule locally. The table is the source of truth for registration, while the local `ReminderData` state is a cache and timer.

## Registration and ownership

`ReminderRegistry` validates names, due times, periods, and the configured minimum period before forwarding the operation to the grain service for the grain's current owner. `LocalReminderService` performs a responsibility check, upserts the row, and reconciles its local entry using the returned ETag. Conditional removal uses the ETag as a concurrency check, so a stale unregister cannot silently delete a newer registration.

The reminder service is a grain service attached to the consistent ring. Ownership changes when membership changes; a new owner discovers the row on its next table read rather than relying on the old silo to transfer an in-memory timer.

## Refresh and reconciliation

After the reminder table starts successfully, the service refreshes the ring range periodically. Reads are staggered and retried during initialization. Each refresh reads the range, compares the table sequence with local mutations, starts or updates entries which are now owned, and stops entries which disappeared or moved elsewhere. A local sequence prevents a concurrent registration or removal from being overwritten by an older refresh result.

This is deliberately reconciliation, not a distributed lock around every tick. A brief ownership overlap during membership convergence is possible; ETags, responsibility checks, and the service's delivery state prevent a stale owner from continuing indefinitely.

## Tick delivery

Each local reminder has a small state machine: stopped, runnable, running, and stopping. Its loop computes the next due time from the stored start time and period, waits using the reminder `TimeProvider`, and invokes the target grain through the normal messaging path. The service counts active deliveries and refuses new ticks after shutdown begins, then waits for active deliveries to quiesce.

A tick is not a durable queue item. If the owner or process fails, the next owner reconstructs the schedule from the row. A slow or unavailable grain can therefore delay a tick, and a process failure can result in a later delivery without a durable record of every missed occurrence. Reminder callbacks should be idempotent and should not assume exactly-once execution.

## Storage and operational boundaries

The in-memory table is suitable for development only. Production tables provide the durability and concurrency behavior required for ownership changes, but their consistency and availability characteristics remain provider-specific. Table startup is part of silo lifecycle initialization; failure to open the configured table prevents normal reminder service startup.

The [timers and reminders guide](../grains/timers-and-reminders.md) owns registration usage. This page explains why reminder persistence, ring movement, refresh intervals, and callback failures affect observed behavior.

Source: [`LocalReminderService`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Reminders/ReminderService/LocalReminderService.cs), [`ReminderRegistry`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Reminders/ReminderService/ReminderRegistry.cs), and [`IReminderTable`](https://github.com/dotnet/orleans/blob/main/src/Orleans.Reminders/SystemTargetInterfaces/IReminderTable.cs).
