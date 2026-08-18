---
title: Client diagnostic events
description: Find current Orleans client event IDs and use symptom-based observability guidance.
ms.date: 08/02/2026
ms.topic: reference
---

# Client diagnostic events

The generated <xref:Orleans.ErrorCode> API reference tracks the runtime's current client event definitions. The [error-code source on the `main` branch](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core.Abstractions/Logging/ErrorCodes.cs) shows definitions under active development. Preserve structured <xref:Microsoft.Extensions.Logging.EventId>, category, exception, trace ID, and span ID fields in your log backend.

Search for the structured event ID in <xref:Orleans.ErrorCode>, then route by its category, exception, and surrounding symptoms:

| Diagnostic area | Remedy |
|---|---|
| Gateway discovery, connection, TLS, or reconnect | [Connection and gateway failures](troubleshooting-symptom-catalog.md#connection-and-gateway-failures) |
| Request timeout or latency | [Request timeouts](troubleshooting-symptom-catalog.md#request-timeouts) |
| Silo unavailable or message rejected | [Silo unavailable or message rejected](troubleshooting-symptom-catalog.md#silo-unavailable-or-message-rejected) |
| Gateway busy, load shedding, or rejection | [Gateway or cluster overload](troubleshooting-symptom-catalog.md#gateway-or-cluster-overload) |
| Serialization or codec | [Serialization failures after a version change](troubleshooting-symptom-catalog.md#serialization-failures-after-a-version-change) |

Base alerts on sustained service symptoms correlated with event IDs and runtime signals. Follow [Interpret Orleans observability signals](signals.md).
