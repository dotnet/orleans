---
title: Messaging delivery guarantees
description: Learn about messaging delivery guarantees in .NET Orleans.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Messaging delivery guarantees

Orleans messaging delivery guarantees are **at-most-once** by default. Optionally, if you configure retries upon timeout, Orleans provides at-least-once delivery instead.

In more detail:

- Every message in Orleans has an automatic timeout (you can configure the exact timeout). If the reply doesn't arrive on time, the returned <xref:System.Threading.Tasks.Task> breaks with a timeout exception.
- You can configure Orleans to perform automatic retries upon timeout. By default, it _does not_ perform automatic retries.
- Your application code can also choose to implement retries upon timeout.

If the Orleans system is configured not to perform automatic retries (the default setting) and your application doesn't resend messages, **Orleans provides at-most-once message delivery**. A message is either delivered once or not at all. **It's never delivered twice.**

In a system with retries (either by the runtime or by the application), the message might arrive multiple times. Orleans currently doesn't durably store which messages have already arrived or suppress subsequent deliveries. (We believe this would be quite costly.) So, in a system with retries, Orleans does NOT guarantee at-most-once delivery.

**If you keep retrying potentially indefinitely**, **the message eventually arrives**, thus providing the at-least-once delivery guarantee. Note that "eventually arrives" is something the runtime needs to guarantee. It doesn't happen automatically just because you keep retrying. Orleans provides eventual delivery because grains never enter a permanent failure state, and a failed grain eventually reactivates on another silo.

**To summarize**: In a system without retries, Orleans guarantees at-most-once message delivery. In a system with infinite retries, Orleans guarantees at-least-once (and _does not_ guarantee at-most-once).

> [!IMPORTANT]
> The [Orleans technical report](https://research.microsoft.com/pubs/210931/Orleans-MSR-TR-2014-41.pdf) accidentally only mentioned the second option with automatic retries. It failed to mention that by default, with no retries, Orleans provides at-most-once delivery.
