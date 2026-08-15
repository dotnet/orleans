---
title: Grain timers and reminders
description: Choose between activation-scoped grain timers and durable reminders in Orleans.
ms.date: 08/21/2026
ms.topic: concept-article
navigation: hidden
---

# Grain timers and reminders

Orleans schedules periodic grain work through two mechanisms with distinct ownership and durability guarantees:

| Mechanism | Owner | Schedule storage | Activation behavior | Typical cadence |
|---|---|---|---|---|
| [Grain timer](timers.md) | One grain activation | Activation memory | Executes on the current activation and ends with it | Frequent work while an activation is active |
| [Reminder](reminders.md) | One logical grain | Configured reminder provider | Activates the grain when a tick is delivered and resumes after cluster recovery | Work measured in minutes, hours, or days |

Choose a grain timer when the work belongs to the current activation. Choose a reminder when the schedule belongs to the grain identity and must survive activation and cluster lifecycle changes.

[Advanced reminders](advanced-reminders.md) provide one-shot and cron schedules, absolute UTC due times, priority, missed-occurrence policies, and administrative paging through an opt-in reminder service.

## Grain timers

Grain timers execute callbacks as grain turns on one activation. The runtime schedules the next tick after the current callback completes, so a timer callback never overlaps itself.

See [Grain timers](timers.md) for registration, callback scheduling, interleaving, activation lifetime, cancellation, and troubleshooting.

<a id="timer-behavior"></a>

The [timer behavior reference](timers.md#timer-behavior) describes the guarantees controlled by <xref:Orleans.Runtime.GrainTimerCreationOptions>.

## Reminders

Reminders persist a schedule for a logical grain. Reminder delivery follows normal grain request scheduling and activates the grain when needed. The provider preserves the schedule while each tick is generated and delivered by the runtime.

See [Reminders](reminders.md) for registration, durable scheduling, timing constraints, provider configuration, missed-tick reconciliation, and troubleshooting.

<a id="reminder-behavior"></a>

The [reminder delivery reference](reminders.md#reminder-behavior) explains durable definitions, volatile tick delivery, reactivation, and recovery.

<a id="reminder-timing-constraints"></a>

The [reminder timing constraints](reminders.md#reminder-timing-constraints) define valid due times and periods.

## Configure reminder storage

Every silo configures one reminder provider. See [Configure reminder storage](reminders.md#configure-reminder-storage) for production providers, development configuration, and provider-specific guidance.

## POCO grains

Grains implementing <xref:Orleans.IGrainBase> directly use the same timer and reminder extension APIs. The dedicated [grain timer](timers.md#poco-grains) and [reminder](reminders.md#poco-grains) pages describe the lower-level registries available through dependency injection.
