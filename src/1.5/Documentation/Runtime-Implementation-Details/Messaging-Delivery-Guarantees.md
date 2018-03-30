---
layout: page
title: Messaging Delivery Guarantees
---

[!include[](../../warning-banner.md)]

# Messaging Delivery Guarantees

Orleans messaging delivery guarantees are **at-most-once**, by default.
Optionally, if configured to do retries upon timeout, Orleans provides at-least-once deliv­ery instead.

In more details:

* Every message in Orleans has automatic timeout (the exact timeout can be configured). If the reply does not arrive on time the return Task is broken with timeout exception.

* Orleans can be configured to do automatic retries upon timeout. By default we do NOT do automatic retries.

* Application code of course can also pick to do retries upon timeout.

If the Orleans system is configured not to do automatic retries (default setting) and application is not resending – **Orleans provides at most once message delivery**. A message will either be delivered once or not at all. **It will never be delivered twice.**

In the system with retries (either by the runtime or by the application) the message may arrive multiple times. Orleans currently does nothing to durably store which messages already arrived and suppress the second delivery (we believe this would be pretty costly). So in the system with retries Orleans does NOT guarantee at most once delivery.

**If you keep retrying potentially indefinitely**, **the message will eventually arrive**, thus providing at least once delivery guarantee. Notice that “will eventually arrive” is something that the runtime needs to guarantee. It does not come for free just by itself even if you keep retrying. Orleans provides eventually delivery since grains never go into any permanent failure state and a failed grain will for sure eventually be re-activated on another silo.

**So to summarize**: in the system without retries Orleans guarantees at most once message delivery. In the system with infinite retries Orleans guarantee at least once (and does NOT guarantee at most once).


**Note**:
In the [Orleans technical report](http://research.microsoft.com/pubs/210931/Orleans-MSR-TR-2014-41.pdf) we accidentally only mentioned the 2nd option with automatic retries and forgot to mention that by default with no retries, Orleans provides at most once delivery.
