---
title: Configure .NET garbage collection
description: Configure .NET garbage collection for Orleans silos.
ms.date: 08/17/2026
ms.topic: how-to
---

# Configure .NET garbage collection

The [.NET garbage collector (GC)](https://learn.microsoft.com/dotnet/standard/garbage-collection/fundamentals) allocates and reclaims managed memory for an Orleans silo. Workload characteristics and available CPU and memory determine the most effective GC configuration.

## Choose workstation or server GC

[Workstation GC and server GC](https://learn.microsoft.com/dotnet/standard/garbage-collection/workstation-server-gc) provide different resource and throughput characteristics:

- Workstation GC performs collections with fewer dedicated GC resources. It suits development and high-density deployments where many small silo processes share a host.
- Server GC performs collections on dedicated threads and scales collection work across multiple heaps. It suits production silos that prioritize throughput and scalability.

[CoreCLR selects workstation GC by default](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#workstation-vs-server). Project SDK settings and deployment environment variables determine whether the process uses that default or requests server GC:

- [`Microsoft.NET.Sdk.Web` requests server GC by default](https://github.com/dotnet/sdk/blob/main/src/WebSdk/ProjectSystem/Targets/Microsoft.NET.Sdk.Web.ProjectSystem.props#L16-L22).
- `Microsoft.NET.Sdk` and [`Microsoft.NET.Sdk.Worker`](https://learn.microsoft.com/dotnet/core/extensions/workers#template-defaults) use the CoreCLR default.
- [Official .NET container images](https://github.com/dotnet/dotnet-docker/blob/main/eng/dockerfile-templates/Dockerfile.common-dotnet-envs) preserve the executable project's generated runtime configuration and apply deployment environment overrides.

A containerized silo built with the Web SDK therefore requests server GC. A silo built with the base or Worker SDK uses workstation GC by default. An explicit setting in the executable silo project keeps the intended mode consistent across project SDKs and container images.

When server GC is active, [dynamic adaptation to application sizes (DATAS)](https://learn.microsoft.com/dotnet/standard/garbage-collection/datas) starts with one heap and adjusts the number as the workload changes, up to the number of processors available to the process. .NET 8 activates DATAS through explicit configuration; .NET 9 and later activate it by default. A fixed [`GCHeapCount`](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#heap-count) selects a fixed number of server GC heaps. On .NET 10, that setting makes `GCDynamicAdaptationMode` report `0`. Use <xref:System.Runtime.GCSettings.IsServerGC> to identify the GC flavor, and use representative load tests to compare memory use, throughput, and pause time. See [Preparing for the .NET 10 GC](https://devblogs.microsoft.com/dotnet/preparing-for-dotnet-10-gc/) for DATAS tradeoffs and tuning guidance.

## Configure the silo project

To request server GC, place the following property in the executable project that hosts the silo. The documentation snippet uses a class library for compilation:

:::code language="xml" source="../snippets/hosting/Hosting.csproj" id="server_gc":::

The [`ServerGarbageCollection` MSBuild property](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#workstation-vs-server) writes `System.GC.Server` to the host's generated `.runtimeconfig.json` file. The runtime reads this file when the host process starts.

[Background GC](https://learn.microsoft.com/dotnet/standard/garbage-collection/background-gc) performs generation 2 collections on dedicated threads while application threads continue running. It is enabled by default for both workstation and server GC. [`ConcurrentGarbageCollection`](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#background-gc) set to `false` selects non-concurrent GC when representative benchmarks favor that mode.

[GC configuration](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector) is read when the runtime initializes. Restart the process after changing a project setting, runtime configuration, or environment variable.

## Container resources

.NET calculates the processors and memory available to a container during runtime startup. One available logical processor selects workstation GC. Two or more available processors allow a server GC request to take effect. With server GC active, DATAS varies the participating heap count from one up to the available processor count as workload demand changes.

Kubernetes CPU requests normally control scheduling and CPU weighting. The [Kubernetes static CPU Manager policy](https://kubernetes.io/docs/tasks/administer-cluster/cpu-management-policies/#static-policy-configuration) assigns exclusive CPU affinity to eligible pods from integer CPU requests. .NET calculates <xref:System.Environment.ProcessorCount> from the machine processor count, process affinity, and CPU utilization limit, rounding a fractional limit up to the next whole processor. `DOTNET_PROCESSOR_COUNT` supplies an explicit value. [Server GC resource settings](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#manage-resource-usage-for-server-gc) configure the heaps and threads that use those processors. See [how .NET determines the available processor count](https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/6.0/environment-processorcount-on-windows).

The container memory limit supplies the physical-memory basis for the default [GC heap hard limit](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#heap-hard-limit). The heap hard limit covers the GC heap and GC bookkeeping. Size the container limit for that managed-memory budget plus native memory, socket buffers, serialization buffers, provider SDKs, telemetry, temporary allocation bursts, and failover growth. This headroom gives the GC capacity to respond to bursts before the platform terminates the process.

Load-test with the same CPU and memory constraints used in production. See [Capacity planning and scaling](../../deployment/capacity-planning.md) and [Kubernetes resource requests and limits](../../deployment/kubernetes.md#resource-requests-and-limits).

## Verify the effective mode

Verify the deployed runtime configuration using these signals:

- `System.GC.Server: true` in the published application's `.runtimeconfig.json` records a server GC request. An absent key applies the CoreCLR workstation GC default.
- At startup, Orleans logs `Silo starting with GC settings: ServerGC={value} GCLatencyMode={value}` and emits an advisory warning for workstation GC.
- At runtime, <xref:System.Runtime.GCSettings.IsServerGC> reports the active GC flavor, and <xref:System.Environment.ProcessorCount> reports the processor count used during runtime initialization.
- With server GC active, <xref:System.GC.GetConfigurationVariables*> reports `GCDynamicAdaptationMode=1` while DATAS adapts the heap count. On .NET 10, a fixed heap count reports `0`.

## Coordinate with Orleans activation management

.NET GC reclaims unreachable managed objects. Orleans [activation collection](activation-collection.md) decides when idle grain activations become unreachable. Configure both layers:

- Select the process GC mode from workload and deployment measurements.
- Orleans makes activations eligible for collection after 15 minutes of inactivity by default; workload measurements guide type-specific age adjustments.
- Enable memory-pressure activation shedding to prioritize older activations for deactivation when process memory exceeds the configured limit and move usage toward the configured target.

Monitor allocation rate, heap size, pause duration, time in GC, process working set, and activation count together. Tune GC settings alongside activation lifetimes, application object retention, and memory-pressure shedding. See [.NET GC performance guidance](https://learn.microsoft.com/dotnet/standard/garbage-collection/performance) and [Orleans runtime signals](../monitoring/signals.md).
