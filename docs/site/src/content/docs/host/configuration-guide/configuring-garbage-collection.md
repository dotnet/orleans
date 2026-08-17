---
title: Configure .NET garbage collection
description: Configure .NET garbage collection for Orleans silos.
ms.date: 08/17/2026
ms.topic: how-to
---

# Configure .NET garbage collection

Orleans uses the [.NET garbage collector (GC)](https://learn.microsoft.com/dotnet/standard/garbage-collection/fundamentals) for managed memory. The best GC mode depends on the workload and the CPU and memory available to each silo process.

## Choose workstation or server GC

[Workstation GC and server GC](https://learn.microsoft.com/dotnet/standard/garbage-collection/workstation-server-gc) optimize for different resource and throughput characteristics:

- Workstation GC is the default for standalone .NET applications. It uses fewer dedicated GC resources and can be appropriate for development or high-density deployments where many small silo processes share a host.
- Server GC is intended for server applications that prioritize throughput and scalability. It is generally the better starting point for production silos.

[Dynamic adaptation to application sizes (DATAS)](https://learn.microsoft.com/dotnet/standard/garbage-collection/datas) is enabled by default. DATAS adjusts the number of heaps as the workload changes, from as few as one heap up to the number of processors available to the process. Therefore, don't infer the GC flavor from processor or heap count. Choose a mode using measurements under representative load rather than host type alone.

## Configure the silo project

Enable server GC in the executable project that hosts the silo. The documentation snippet project is a class library so that the example can be compiled, but an application must put this property in its silo host executable project:

:::code language="xml" source="../snippets/hosting/Hosting.csproj" id="server_gc":::

The [`ServerGarbageCollection` MSBuild property](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#workstation-vs-server) writes `System.GC.Server` to the generated `.runtimeconfig.json` file. Setting it only in a referenced grain class library doesn't configure the host process.

[Background GC](https://learn.microsoft.com/dotnet/standard/garbage-collection/background-gc) is enabled by default for both workstation and server GC. The [`ConcurrentGarbageCollection` MSBuild property](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#background-gc) controls that setting, but Orleans doesn't require it to be specified. Set it to `false` only when representative benchmarks show that non-concurrent GC is preferable.

[GC configuration](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector) is read when the runtime initializes. Restart the process after changing a project setting, runtime configuration, or environment variable.

## Containers and CPU limits

.NET considers the processors and memory available to a container when configuring the GC. The available processor count caps the number of heaps that DATAS can use, but DATAS can select fewer heaps as demand changes. CPU requests reserve scheduling capacity but don't constrain the processor count; CPU limits and [server GC resource settings](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#manage-resource-usage-for-server-gc) can.

In a memory-constrained environment, the runtime uses the container memory limit when calculating its default [GC heap hard limit](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#heap-hard-limit). That limit covers the GC heap and GC bookkeeping, not all process memory. Include native memory, socket buffers, serialization buffers, provider SDKs, telemetry, and temporary allocation bursts when setting the container limit. Leave headroom so the GC can respond to bursts before the platform terminates the process.

Load-test with the same CPU and memory constraints used in production. See [Capacity planning and scaling](../../deployment/capacity-planning.md) and [Kubernetes resource requests and limits](../../deployment/kubernetes.md#resource-requests-and-limits).

## Verify the effective mode

Don't infer the active GC mode only from the project file:

- At startup, Orleans logs `Silo starting with GC settings: ServerGC={value} GCLatencyMode={value}`. It also logs an advisory warning when the runtime reports workstation GC.
- At runtime, <xref:System.Runtime.GCSettings.IsServerGC> reports whether server GC is active. Log or expose this value through the application's diagnostics when operators need to verify a deployed instance. Don't use the current heap count as a proxy for the GC flavor because DATAS changes that count dynamically.
- The published application's `.runtimeconfig.json` shows the generated setting, but runtime observation confirms the effective mode after host, environment, and resource constraints are applied.

## Coordinate with Orleans activation management

.NET GC reclaims unreachable managed objects. Orleans [activation collection](activation-collection.md) decides when idle grain activations become unreachable. Configure both layers:

- Select the process GC mode from workload and deployment measurements.
- Keep activation collection defaults unless workload measurements indicate a different idle age.
- Consider memory-pressure activation shedding to reduce active grain count before the process reaches its memory limit.

Monitor allocation rate, heap size, pause duration, time in GC, process working set, and activation count together. See [.NET GC performance guidance](https://learn.microsoft.com/dotnet/standard/garbage-collection/performance) and [Orleans runtime signals](../monitoring/signals.md). A GC setting can't compensate for unbounded activation growth or retained application objects.
