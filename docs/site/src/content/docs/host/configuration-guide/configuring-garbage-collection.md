---
title: Configure .NET garbage collection
description: Configure .NET garbage collection for Orleans silos.
ms.date: 08/02/2026
ms.topic: how-to
---

# Configure .NET garbage collection

Orleans silos are long-running, highly concurrent server processes. Enable server garbage collection in the silo project:

```xml
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
</PropertyGroup>
```

Server GC creates heaps and collection threads based on the processors available to the process. It is effective only when more than one processor is available.

## Containers and CPU limits

.NET considers container CPU and memory limits when configuring the GC. Give the silo more than one CPU when you expect server GC to provide parallel collection, and load-test using the same limits as production.

Don't size a silo solely from average managed-heap usage. Include native memory, socket buffers, serialization buffers, provider SDKs, telemetry, and temporary allocation bursts. Leave headroom below the container or operating-system memory limit so the runtime can collect before the process is terminated.

## Coordinate with Orleans activation management

.NET GC reclaims unreachable managed objects. Orleans [activation collection](activation-collection.md) decides when idle grain activations become unreachable. Configure both layers:

- Use server GC for process-level managed memory throughput.
- Keep activation collection defaults unless workload measurements indicate a different idle age.
- Consider memory-pressure activation shedding to reduce active grain count before the process reaches its memory limit.

Monitor allocation rate, heap size, pause duration, time in GC, process working set, and activation count together. A GC setting can't compensate for unbounded activation growth or retained application objects.

For runtime configuration details, see [.NET garbage collection configuration](../../../core/runtime-config/garbage-collector.md) and <xref:System.Runtime.GCSettings.IsServerGC>.
