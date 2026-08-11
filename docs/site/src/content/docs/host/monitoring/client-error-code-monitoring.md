---
title: Client diagnostic events
description: Find current Orleans client event IDs and use symptom-based observability guidance.
ms.date: 08/02/2026
ms.topic: reference
---

# Client diagnostic events

The previous hand-maintained client error-code table was removed because it drifted from the runtime and encouraged alerts on numeric ranges without service context.

For current definitions, use the generated <xref:Orleans.ErrorCode> API reference. To inspect definitions under active development, see the [error-code source on the `main` branch](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core.Abstractions/Logging/ErrorCodes.cs). Preserve structured <xref:Microsoft.Extensions.Logging.EventId>, category, exception, trace ID, and span ID fields in your log backend.

Search for the structured event ID in <xref:Orleans.ErrorCode>, then route by its category, exception, and surrounding symptoms:

| Diagnostic area | Remedy |
|---|---|
| Gateway discovery, connection, TLS, or reconnect | [Connection and gateway failures](troubleshooting-symptom-catalog.md#connection-and-gateway-failures) |
| Request timeout or latency | [Request timeouts](troubleshooting-symptom-catalog.md#request-timeouts) |
| Silo unavailable or message rejected | [Silo unavailable or message rejected](troubleshooting-symptom-catalog.md#silo-unavailable-or-message-rejected) |
| Gateway busy, load shedding, or rejection | [Gateway or cluster overload](troubleshooting-symptom-catalog.md#gateway-or-cluster-overload) |
| Serialization or codec | [Serialization failures after a version change](troubleshooting-symptom-catalog.md#serialization-failures-after-a-version-change) |

Don't alert on a numeric range alone. Follow [Interpret Orleans observability signals](signals.md) to combine event IDs with sustained service symptoms.
