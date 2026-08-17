---
title: Silo diagnostic events
description: Find current Orleans silo event IDs and use symptom-based observability guidance.
ms.date: 08/02/2026
ms.topic: reference
---

# Silo diagnostic events

The generated <xref:Orleans.ErrorCode> API reference tracks the runtime's current silo event definitions. The [error-code source on the `main` branch](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core.Abstractions/Logging/ErrorCodes.cs) shows definitions under active development.

Search for the structured event ID in <xref:Orleans.ErrorCode>, then use its log category, exception, and surrounding metrics to select a remedy:

| Diagnostic area | Remedy |
|---|---|
| Membership, missed pings, silo status, or grain directory | [Membership and directory churn](troubleshooting-symptom-catalog.md#membership-and-directory-churn) |
| Message rejection, unavailable silo, connection, or gateway | [Silo unavailable or message rejected](troubleshooting-symptom-catalog.md#silo-unavailable-or-message-rejected) |
| Scheduler or long-running activation turn | [Long-running or deadlocked grain turns](troubleshooting-symptom-catalog.md#long-running-or-deadlocked-grain-turns) |
| Storage provider, state version, or throttling | [Storage consistency failures](troubleshooting-symptom-catalog.md#storage-consistency-failures) and [Storage throttling](troubleshooting-symptom-catalog.md#storage-throttling) |
| Serialization or codec | [Serialization failures after a version change](troubleshooting-symptom-catalog.md#serialization-failures-after-a-version-change) |
| Reminder or timer | [Reminder and timer timing](troubleshooting-symptom-catalog.md#reminder-and-timer-timing) |
| Overload, rejection, memory, or shutdown | [Orleans symptom and signal catalog](troubleshooting-symptom-catalog.md) |

Base alerts on sustained service symptoms correlated with event IDs and runtime signals. Follow [Interpret Orleans observability signals](signals.md).
