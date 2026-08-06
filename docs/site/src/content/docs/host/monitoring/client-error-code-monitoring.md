---
title: Client diagnostic events
description: Find current Orleans client event IDs and use symptom-based observability guidance.
ms.date: 08/02/2026
ms.topic: reference
---

# Client diagnostic events

The previous hand-maintained client error-code table was removed because it drifted from the runtime and encouraged alerts on numeric ranges without service context.

For current definitions, use the generated <xref:Orleans.ErrorCode> API reference. To inspect definitions under active development, see the [error-code source on the `main` branch](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core.Abstractions/Logging/ErrorCodes.cs). Preserve structured `EventId`, category, exception, trace ID, and span ID fields in your log backend.

For alert design and diagnosis, see:

- [Interpret Orleans observability signals](signals.md)
- [Client can't connect](troubleshooting.md#client-cant-connect)
- [Grain calls time out](troubleshooting.md#grain-calls-time-out)
