---
title: Silo diagnostic events
description: Find current Orleans silo event IDs and use symptom-based observability guidance.
ms.date: 08/02/2026
ms.topic: reference
---

# Silo diagnostic events

The previous hand-maintained silo error-code table was removed because it drifted from the runtime and mixed implementation event IDs with operational alert thresholds.

For current definitions, use the generated <xref:Orleans.ErrorCode> API reference or the [error-code source for the Orleans version you deploy](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core.Abstractions/Logging/ErrorCodes.cs). Alert on sustained service symptoms and use event IDs to narrow an investigation.

For current workflows, see:

- [Interpret Orleans observability signals](signals.md)
- [Troubleshoot Orleans incidents](troubleshooting.md)
