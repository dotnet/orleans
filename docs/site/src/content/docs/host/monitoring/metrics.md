---
title: Monitor Orleans metrics
description: Interpret current Orleans client and silo metrics, instrument types, units, dimensions, and health indicators.
ms.date: 08/15/2026
ms.topic: concept-article
---

# Monitor Orleans metrics

Orleans emits metrics from a <xref:System.Diagnostics.Metrics.Meter> named `Microsoft.Orleans`. The instrument names and semantics on this page describe the current runtime implementation. Discover the instruments emitted by the deployed Orleans version because releases and provider packages can differ.

```dotnetcli
dotnet-counters monitor -n <ProcessName> --counters Microsoft.Orleans
```

The names below are .NET instrument names. An exporter can normalize them for its backend, for example by replacing hyphens with underscores. See the [complete Orleans metrics catalog](metrics-catalog.md) for every current instrument and its source-derived description. To inspect definitions under active development, see [InstrumentNames.cs](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/Diagnostics/Metrics/InstrumentNames.cs) and the adjacent instrument implementations.

## Interpret instrument types

The instrument type determines which query is meaningful. Don't alert on every displayed "current" value without checking the instrument type and exporter temporality.

| Instrument type | Orleans use | Interpret it as |
|---|---|---|
| Counter or observable counter | Completed events, failures, bytes, and preaggregated request-latency data | A monotonically increasing total for one process lifetime. Query the increase or rate over a window. Account for resets when a process restarts. |
| Up/down counter | Connected clients and grain instances | A running total which can increase or decrease. Query the latest aggregated value, not its rate, when the question is "how many exist now?" |
| Observable gauge | Connected gateways, activations, working-set members, and available memory | A point-in-time observation. Aggregate intentionally: a minimum often finds an unhealthy instance, while a sum gives a cluster total for counts. |
| Histogram | Storage, activation, deactivation, message-size, and reminder-tardiness distributions | A distribution of observations. Use count for volume and buckets or percentiles for tail behavior. Configure backend bucket boundaries which cover expected values. |

OpenTelemetry can transport sums using cumulative or delta temporality. "Current" and "Delta" fields shown by older telemetry exporters were views over a counter, not separate Orleans instruments. Current OpenTelemetry guidance similarly distinguishes monotonic sums, gauges, and histograms and allows [temporality conversion and reaggregation](https://opentelemetry.io/docs/specs/otel/metrics/data-model/).

Units in the tables describe each instrument's natural unit. Orleans doesn't set unit metadata on every instrument, so `count` can be the natural unit even when exporters receive no unit string. Where unit metadata is emitted, some established instruments use historical strings such as `ms`, `seconds`, `bytes`, and `MB`, while current [OpenTelemetry metric semantic conventions](https://opentelemetry.io/docs/specs/semconv/general/metrics/) generally recommend UCUM units such as `s` and `By`. Preserve emitted units in storage and convert explicitly in queries instead of inferring a unit from the metric name.

## Client signals

Start with the caller's view of availability and latency. A healthy silo cannot compensate for a client which has no route to the cluster.

Caller-side request instruments are emitted by standalone clients and by silos when they issue grain calls.

| Instrument | Type and unit | Meaning and interpretation |
|---|---|---|
| `orleans-client-connected-gateways` | Observable gauge, count | Number of gateway connections held by this client. Alert when it remains zero beyond the expected reconnect window. A reduction which remains above zero indicates less path redundancy and should be correlated with gateway and network signals. |
| `orleans-app-requests-latency-count` | Observable counter, count | Completed outbound request callbacks observed by the caller. Use its increase as the request volume denominator. |
| `orleans-app-requests-latency-sum` | Observable counter, milliseconds, without unit metadata | Cumulative caller-observed elapsed time. Divide the increase in `sum` by the increase in `count` for a windowed mean, but use the bucket series for tail behavior. |
| `orleans-app-requests-latency-bucket` | Observable counter, milliseconds encoded by the `duration` attribute | Preaggregated latency bands with boundaries from `1ms` through `15000ms`, plus an overflow band. Each series counts observations in its own band, rather than cumulative observations less than or equal to the boundary. Compute a distribution from increases in all bands over the same window. |
| `orleans-app-requests-timedout` | Counter, count; `grain_type` | Requests which exceeded the configured response timeout. Alert on a volume-gated ratio against completed requests, not one timeout or the lifetime total. |
| `orleans-app-requests-canceled` | Counter, count; `grain_type` | Requests canceled by their caller. Separate expected cancellation from service failure using application context and traces. |

Caller-observed request latency includes time until the callback completes, so it can include transport, queueing, grain execution, storage, and response delivery. It also records terminal paths such as timeout, cancellation, target-silo failure, and host shutdown. Use traces and the [incident runbooks](troubleshooting.md) to locate the delay.

## Silo signals

### Admission, messaging, and membership

| Instrument | Type and unit | Meaning and interpretation |
|---|---|---|
| `orleans-gateway-connected-clients` | Up/down counter, count | Current client connections on this gateway after aggregation. Sudden loss across gateways can indicate a rollout or network break; sustained skew can indicate uneven routing. |
| `orleans-gateway-load-shedding` | Counter, count | Requests rejected because the gateway's overload detector reported saturation. Any sustained nonzero rate indicates user-visible admission pressure. |
| `orleans-messaging-rejected` | Counter, count; `Direction` | Rejected messages. Correlate the rate with load shedding, request timeouts, CPU, memory, and dependency latency. |
| `orleans-messaging-sent-failed` | Counter, count; `Direction` | Messages which couldn't be sent. Correlate by instance and direction with socket churn and remote-silo logs. |
| `orleans-messaging-sent-dropped` | Counter, count; `Direction` | Messages intentionally dropped by the runtime. A sustained increase requires log and trace investigation. |
| `orleans-messaging-expired` | Counter, count; `Phase` | Messages which expired during `Send`, `Receive`, `Dispatch`, `Invoke`, or `Respond`. The phase narrows whether delay occurred before or during execution. |
| `orleans-messaging-pings-reply-missed` | Counter, count; `Destination` | Failed direct or indirect membership probes. Alert on a sustained increase or missed-reply ratio and correlate with membership transitions. Isolated misses can occur during pauses, restarts, or transient packet loss. |
| `orleans-networking-sockets-opened` / `orleans-networking-sockets-closed` | Counters, count; `Direction` | Connection churn. Compare their rates and correlate with send failures. Their difference is only meaningful within one process lifetime. |

Message-size instruments `orleans-messaging-sent-messages-size` and `orleans-messaging-received-messages-size` are histograms in `bytes`. Use them for payload growth and bandwidth diagnosis, not primary availability alerts. They include `ConnectionDirection`, `MessageDirection`, and sometimes a remote `silo` attribute.

### Scheduling and activations

| Instrument | Type and unit | Meaning and interpretation |
|---|---|---|
| `orleans-scheduler-long-running-turns` | Counter, count | Grain micro-turns whose synchronous execution exceeded <xref:Orleans.Configuration.SchedulingOptions.TurnWarningLengthThreshold>. The default is one second. The lifetime total naturally only increases; alert on its rate and correlate with latency, CPU, thread-pool, traces, and warning logs. |
| `orleans-catalog-activations` | Observable gauge, count | Activations currently registered on this silo. Trend it against memory and traffic. A high value isn't intrinsically unhealthy; unexpected growth, imbalance, or churn is the useful signal. |
| `orleans-catalog-activation-working-set` | Observable gauge, count | Activations in the local active working set. Compare it with total activations to understand the active portion of the catalog. |
| `orleans-grains` | Up/down counter, count; `type` | Current grain instances by grain type after aggregation. Use it to identify placement imbalance or a grain type driving activation growth. |
| `orleans-catalog-activation-created` / `orleans-catalog-activation-destroyed` | Counters, count | Activation lifecycle throughput. High rates in both directions indicate churn even when the current activation gauge is flat. |
| `orleans-catalog-activation-latency` | Histogram, `ms`; `status`, `directory` | Activation duration and outcome. Break down non-success statuses (`canceled`, `directory_error`, `duplicate`, or `error`) and correlate tail latency with directory and storage health. |
| `orleans-catalog-activation-failed-to-activate` | Counter, count | Activation attempts which failed to construct or initialize an activation. A sustained increase is an application availability signal. |

A long-running turn means Orleans observed one scheduled work item executing synchronously beyond the configured warning threshold. It doesn't by itself prove a deadlock. Common causes include synchronous blocking, lock contention, CPU-heavy work, or blocking I/O. See [Grain turns appear stuck](troubleshooting.md#grain-turns-appear-stuck).

### Storage

| Instrument | Type and unit | Meaning and interpretation |
|---|---|---|
| `orleans-storage-read-latency` | Histogram, `ms` | Successfully completed grain-state read latency. |
| `orleans-storage-write-latency` | Histogram, `ms` | Successfully completed grain-state write latency. |
| `orleans-storage-clear-latency` | Histogram, `ms` | Successfully completed grain-state clear latency. |
| `orleans-storage-read-errors` | Counter, count | Read failures. |
| `orleans-storage-write-errors` | Counter, count | Write failures. |
| `orleans-storage-clear-errors` | Counter, count | Clear failures. |

All storage instruments use `provider_type_name`, `state_name`, and `state_type`. The histogram count is successful-operation volume. For a windowed error ratio, divide the error-counter increase by the sum of the matching histogram-count increase and error-counter increase. Percentiles identify tail latency which an average hides. Compare Orleans latency with the provider's own throttling, capacity, and service metrics.

### Runtime resources and watchdog

| Instrument | Type and unit | Meaning and interpretation |
|---|---|---|
| `orleans-runtime-available-memory` | Observable gauge, `MB` | GC-reported available memory budget. Values are calculated using 1,024² bytes per reported MB. Alert on the ratio to the total budget and correlate with working set, GC heap, allocation rate, and container limits. |
| `orleans-runtime-total-physical-memory` | Observable gauge, `MB` | Despite the historical name, the implementation reports <xref:System.GC.GetGCMemoryInfo> <xref:System.GCMemoryInfo.TotalAvailableMemoryBytes>, which can reflect a configured or container memory limit rather than host physical RAM. |
| `orleans-watchdog-health-checks` | Counter, count | Runtime watchdog checks performed. |
| `orleans-watchdog-health-checks-failed` | Counter, count | Failed runtime watchdog checks. Alert on failures while confirming that checks continue to run. |

Collect the [built-in `System.Runtime` metrics](https://learn.microsoft.com/dotnet/core/diagnostics/built-in-metrics-runtime) alongside Orleans. At minimum, monitor process CPU time, working set, allocation rate, GC heap size and fragmentation by generation, GC pause time, thread-pool queue length and thread count, lock contention, exception count, and socket counts. A rising Orleans latency signal becomes actionable when correlated with the constrained runtime resource.

## Dimensions and cardinality

Metric identity includes resource attributes and instrument attributes. Set stable resource attributes such as `service.name`, `service.version`, `service.instance.id`, and `deployment.environment.name`. Add cluster, region, and deployment-ring identity only when values are bounded and permitted by the telemetry data policy.

Useful Orleans dimensions are bounded operational categories:

- `grain_type` or `type` for deployed grain types.
- `Direction`, `ConnectionDirection`, `MessageDirection`, and `Phase`.
- `status`, `directory`, and `via` for lifecycle outcomes.
- `provider_type_name`, `state_name`, and `state_type` for configured storage.

Use caution with `Destination` and `silo`. Their values include silo addresses and incarnations, so rolling restarts and unstable membership create new series. Retain them for short-lived diagnosis or use an OpenTelemetry view or Collector processor to remove them from long-term aggregates. Don't add grain keys, request or trace IDs, user or tenant IDs, exception messages, or other unbounded values as metric attributes.

Analyze each important signal both per instance and across the cluster:

- **Minimum per instance** finds a disconnected client or a silo low on available memory.
- **Maximum per instance** finds a hot silo, slow provider path, or placement imbalance.
- **Cluster sum or rate** measures total demand, errors, and capacity.
- **Deployment-version comparison** identifies regressions during rollout.

## Build alerts from symptoms

Prefer ratios, rates, and sustained windows over lifetime totals:

- Page when connected gateways remain zero, request timeout/error ratios violate the service objective, or gateway load shedding is sustained.
- Page when missed probes coincide with lost membership or reduced redundancy, not for one missed reply.
- Page when storage error ratios or tail latency cause activation or request failures.
- Page when available-memory headroom is nearly exhausted or GC pauses coincide with stalled progress.
- Create a capacity alert before activation growth, CPU, queueing, or long-running turns reach the load-shedding point.
- Route isolated failures, per-grain-type imbalance, and gradual resource trends to dashboards or tickets unless user impact is present.

Gate latency and error-ratio alerts on enough request volume to avoid noisy percentages. Establish thresholds from normal deployment and load-test baselines instead of copying universal numeric values.

For investigation sequences, see [Troubleshoot Orleans incidents](troubleshooting.md). For startup, readiness, liveness, and dependency health, see [Health and observability](../../deployment/health-and-observability.md).
