---
title: Configure .NET garbage collection
description: Configure .NET garbage collection for Orleans silos.
ms.date: 08/15/2026
ms.topic: how-to
---

# Configure .NET garbage collection

Orleans uses the .NET garbage collector (GC) for managed memory. The best GC mode depends on the workload and the CPU and memory available to each silo process.

## Choose workstation or server GC

[Workstation GC and server GC](https://learn.microsoft.com/dotnet/standard/garbage-collection/workstation-server-gc) optimize for different resource and throughput characteristics:

- Workstation GC is the default for standalone .NET applications. It uses fewer dedicated GC resources and can be appropriate for development, single-CPU instances, or high-density deployments where many small silo processes share a host.
- Server GC creates a managed heap and dedicated collection thread for each logical processor available to the process. It is generally the better starting point for multi-core production silos that prioritize throughput and scalability.

.NET always uses workstation GC when only one logical processor is available. For other deployments, choose a mode using measurements under representative load rather than host type alone.

## Configure the silo project

Enable server GC in the executable project that hosts the silo:

:::code language="xml" source="../snippets/hosting/Hosting.csproj" id="server_gc":::

The `ServerGarbageCollection` MSBuild property writes `System.GC.Server` to the generated `.runtimeconfig.json` file. Put the property in the executable host project, not only in a grain class library.

[Background GC](https://learn.microsoft.com/dotnet/standard/garbage-collection/background-gc) is enabled by default for both workstation and server GC. The `<ConcurrentGarbageCollection>` property controls that setting, but Orleans doesn't require it to be specified. Set it to `false` only when representative benchmarks show that non-concurrent GC is preferable.

GC configuration is read when the runtime initializes. Restart the process after changing a project setting, runtime configuration, or environment variable.

## Containers and CPU limits

.NET considers the processors and memory available to a container when configuring the GC. Server GC requires more than one logical processor, and its heap count is based on the processors available to the process. CPU requests reserve scheduling capacity but don't constrain the processor count; CPU limits and runtime settings can.

The runtime also uses the container memory limit when calculating its managed-heap budget. That budget doesn't include all process memory. Include native memory, socket buffers, serialization buffers, provider SDKs, telemetry, and temporary allocation bursts when setting the container limit. Leave headroom so the GC can respond to bursts before the platform terminates the process.

Load-test with the same CPU and memory constraints used in production. See [Capacity planning and scaling](../../deployment/capacity-planning.md) and [Kubernetes resource requests and limits](../../deployment/kubernetes.md#resource-requests-and-limits).

## Verify the effective mode

Don't infer the active GC mode only from the project file:

- At startup, Orleans logs `Silo starting with GC settings: ServerGC={value} GCLatencyMode={value}`. It also logs a warning when the runtime reports workstation GC. Workstation GC is expected when the process has only one logical processor.
- At runtime, <xref:System.Runtime.GCSettings.IsServerGC> reports whether server GC is active. Log or expose this value through the application's diagnostics when operators need to verify a deployed instance.
- The published application's `.runtimeconfig.json` shows the generated setting, but runtime observation confirms the effective mode after host, environment, and resource constraints are applied.

## Coordinate with Orleans activation management

.NET GC reclaims unreachable managed objects. Orleans [activation collection](activation-collection.md) decides when idle grain activations become unreachable. Configure both layers:

- Select the process GC mode from workload and deployment measurements.
- Keep activation collection defaults unless workload measurements indicate a different idle age.
- Consider memory-pressure activation shedding to reduce active grain count before the process reaches its memory limit.

Monitor allocation rate, heap size, pause duration, time in GC, process working set, and activation count together. A GC setting can't compensate for unbounded activation growth or retained application objects.

For all supported project, runtime configuration, and environment settings, see [.NET garbage collection configuration](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector).
