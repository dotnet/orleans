---
title: Orleans observability
description: Explore the various runtime monitoring, logging, distributed tracing, and metrics options available in .NET Orleans.
ms.date: 05/23/2025
ms.topic: overview
zone_pivot_groups: orleans-version
ms.custom: sfi-image-nochange
---

# Orleans observability

Observability is one of the most important aspects of a distributed system. It's the ability to understand the system's state at any given time. You can achieve this in various ways, including logging, metrics, and distributed tracing.

## Logging

Orleans uses [Microsoft.Extensions.Logging](https://www.nuget.org/packages/Microsoft.Extensions.Logging) for all silo and client logs. You can use any logging provider compatible with `Microsoft.Extensions.Logging`. Your app code relies on [dependency injection](../../../core/extensions/dependency-injection/overview.md) to get an instance of <xref:Microsoft.Extensions.Logging.ILogger`1> and uses it to log messages. For more information, see [Logging in .NET](../../../core/extensions/logging/overview.md).

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0,orleans-7-0"

## Metrics

Metrics are numerical measurements reported over time. You most often use them to monitor an application's health and generate alerts. For more information, see [Metrics in .NET](../../../core/diagnostics/metrics.md). Orleans uses the [System.Diagnostics.Metrics](../../../core/diagnostics/compare-metric-apis.md#systemdiagnosticsmetrics) APIs to collect metrics. These metrics are exposed to the [OpenTelemetry](https://opentelemetry.io) project, which exports them to various monitoring systems.

To monitor your app without making any code changes, use the `dotnet counters` .NET diagnostic tool. To monitor Orleans <xref:System.Diagnostics.ActivitySource> counters for a specific `<ProcessName>`, use the `dotnet counters monitor` command as shown:

```dotnetcli
dotnet counters monitor -n <ProcessName> --counters Microsoft.Orleans
```

Imagine you're running the [Orleans GPS Tracker sample app](/samples/dotnet/samples/orleans-gps-device-tracker-sample) and monitoring it in a separate terminal with the `dotnet counters monitor` command. The following output is typical:

```dotnetcli
Press p to pause, r to resume, q to quit.
    Status: Running
[Microsoft.Orleans]
    orleans-app-requests-latency-bucket (Count / 1 sec)                    0
        duration=10000ms                                                   0
        duration=1000ms                                                    0
        duration=100ms                                                     0
        duration=10ms                                                      0
        duration=15000ms                                                   0
        duration=1500ms                                                    0
        duration=1ms                                                   2,530
        duration=2000ms                                                    0
        duration=200ms                                                     0
        duration=2ms                                                       0
        duration=400ms                                                     0
        duration=4ms                                                       0
        duration=5000ms                                                    0
        duration=50ms                                                      0
        duration=6ms                                                       0
        duration=800ms                                                     0
        duration=8ms                                                       0
        duration=9223372036854775807ms                                     0
    orleans-app-requests-latency-count (Count / 1 sec)                 2,530
    orleans-app-requests-latency-sum (Count / 1 sec)                       0
    orleans-catalog-activation-working-set                                36
    orleans-catalog-activations                                           38
    orleans-consistent-ring-range-percentage-average                     100
    orleans-consistent-ring-range-percentage-local                       100
    orleans-consistent-ring-size                                           1
    orleans-directory-cache-size                                          27
    orleans-directory-partition-size                                      26
    orleans-directory-ring-local-portion-average-percentage              100
    orleans-directory-ring-local-portion-distance                          0
    orleans-directory-ring-local-portion-percentage                        0
    orleans-directory-ring-size                                        1,295
    orleans-gateway-received (Count / 1 sec)                           1,291
    orleans-gateway-sent (Count / 1 sec)                               2,582
    orleans-messaging-processing-activation-data                           0
    orleans-messaging-processing-dispatcher-forwarded (Count / 1           0
    orleans-messaging-processing-dispatcher-processed (Count / 1       2,543
        Direction=Request,Status=Ok                                    2,582
    orleans-messaging-processing-dispatcher-received (Count / 1        1,271
        Context=Grain,Direction=Request                                1,291
        Context=None,Direction=Request                                 1,291
    orleans-messaging-processing-ima-enqueued (Count / 1 sec)          5,113
```

For more information, see [Investigate performance counters (dotnet-counters)](../../../core/diagnostics/dotnet-counters.md).

### Orleans meters

Orleans uses the [System.Diagnostics.Metrics](../../../core/diagnostics/compare-metric-apis.md#systemdiagnosticsmetrics) APIs to collect metrics. Orleans categorizes each meter into domain-centric concerns, such as networking, messaging, gateway, etc. The following subsections describe the meters Orleans uses.

#### Networking

The following table shows a collection of networking meters used to monitor the Orleans networking layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-networking-sockets-closed` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of sockets that have closed. |
| `orleans-networking-sockets-opened` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of sockets that have opened. |

#### Messaging

The following table shows a collection of messaging meters used to monitor the Orleans messaging layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-messaging-sent-messages-size` | <xref:System.Diagnostics.Metrics.Histogram`1> | A histogram representing the size of messages in bytes that have been sent. |
| `orleans-messaging-received-messages-size` | <xref:System.Diagnostics.Metrics.Histogram`1> | A histogram representing the size of messages in bytes that have been received. |
| `orleans-messaging-sent-header-size` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of header bytes sent. |
| `orleans-messaging-received-header-size` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of header bytes received. |
| `orleans-messaging-sent-failed` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of failed sent messages. |
| `orleans-messaging-sent-dropped` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of dropped sent messages. |
| `orleans-messaging-processing-dispatcher-received` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number dispatcher received messages. |
| `orleans-messaging-processing-dispatcher-processed` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number dispatcher processed messages. |
| `orleans-messaging-processing-dispatcher-forwarded` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number dispatcher forwarded messages. |
| `orleans-messaging-processing-ima-received` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of incoming messages received. |
| `orleans-messaging-processing-ima-enqueued` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of incoming messages enqueued. |
| `orleans-messaging-processing-activation-data` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing all of the processing activation data. |
| `orleans-messaging-pings-sent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of pings sent. |
| `orleans-messaging-pings-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of pings received. |
| `orleans-messaging-pings-reply-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of ping replies received. |
| `orleans-messaging-pings-reply-missed` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of ping replies missed. |
| `orleans-messaging-expired"` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of messages that have expired. |
| `orleans-messaging-rejected` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of messages that have been rejected. |
| `orleans-messaging-rerouted` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of messages that have been rerouted. |
| `orleans-messaging-sent-local` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of local messages sent. |

#### Gateway

The following table shows a collection of gateway meters used to monitor the Orleans gateway layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-gateway-connected-clients` | <xref:System.Diagnostics.Metrics.UpDownCounter`1> | An up/down counter representing the number of connected clients. |
| `orleans-gateway-sent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of gateway messages sent. |
| `orleans-gateway-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of gateway messages received. |
| `orleans-gateway-load-shedding` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of gateway (load shedding) messages that have been rejected due to the gateway being overloaded. |

#### Runtime

The following table shows a collection of runtime meters used to monitor the Orleans runtime layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-scheduler-long-running-turns` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of long running turns within the scheduler. |
| `orleans-runtime-total-physical-memory` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the total number of memory (in MB) of the Orleans runtime. |
| `orleans-runtime-available-memory` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the available memory (in MB) for the Orleans runtime. |

#### Catalog

The following table shows a collection of catalog meters used to monitor the Orleans catalog layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-catalog-activations` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the number of catalog activations. |
| `orleans-catalog-activation-working-set` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the number of activations within the working set. |
| `orleans-catalog-activation-created` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of created activations. |
| `orleans-catalog-activation-destroyed` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of destroyed activations. |
| `orleans-catalog-activation-failed-to-activate` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of activations that failed to activate. |
| `orleans-catalog-activation-collections` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of idle activation collections. |
| `orleans-catalog-activation-shutdown` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of shutdown activations. |
| `orleans-catalog-activation-non-existent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of non-existent activations. |
| `orleans-catalog-activation-concurrent-registration-attempts` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of concurrent activation registration attempts. |

#### Directory

The following table shows a collection of directory meters used to monitor the Orleans directory layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-directory-lookups-local-issued` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of local lookups issued. |
| `orleans-directory-lookups-local-successes` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of local successful lookups. |
| `orleans-directory-lookups-full-issued` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of full directory lookups issued. |
| `orleans-directory-lookups-remote-sent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of remote directory lookups sent. |
| `orleans-directory-lookups-remote-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of remote directory lookups received. |
| `orleans-directory-lookups-local-directory-issued` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of local directory lookups issued. |
| `orleans-directory-lookups-local-directory-successes` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of local directory successful lookups. |
| `orleans-directory-lookups-cache-issued` | <xref:System.Diagnostics.Metrics.Counter`1> | A count cached lookups issued. |
| `orleans-directory-lookups-cache-successes` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of cached successful lookups. |
| `orleans-directory-validations-cache-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory cache validations received. |
| `orleans-directory-partition-size` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the directory partition size. |
| `orleans-directory-cache-size` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the directory cache size. |
| `orleans-directory-ring-size` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the directory ring size. |
| `orleans-directory-ring-local-portion-distance` |<xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the ring range owned by the local directory partition. |
| `orleans-directory-ring-local-portion-percentage` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the ring range owned by the local directory, represented as a percentage of the total range. |
| `orleans-directory-ring-local-portion-average-percentage` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the average percentage of the directory ring range owned by each silo, giving a representation of how balanced directory ownership. |
| `orleans-directory-registrations-single-act-issued` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory single activation registrations issued. |
| `orleans-directory-registrations-single-act-local` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory single activation registrations handled by the local directory partition. |
| `orleans-directory-registrations-single-act-remote-sent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory single activation registrations sent to a remote directory partition. |
| `orleans-directory-registrations-single-act-remote-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory single activation registrations received from remote hosts. |
| `orleans-directory-unregistrations-issued` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory deregistrations issued. |
| `orleans-directory-unregistrations-local` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory deregistrations handled by the local directory partition. |
| `orleans-directory-unregistrations-remote-sent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory deregistrations sent to remote directory partitions. |
| `orleans-directory-unregistrations-remote-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory deregistrations received from remote hosts. |
| `orleans-directory-unregistrations-many-issued` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory multi-activation deregistrations issued. |
| `orleans-directory-unregistrations-many-remote-sent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory multi-activations deregistrations sent to remote directory partitions. |
| `orleans-directory-unregistrations-many-remote-received` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of directory multi-activation deregistrations received from remote hosts. |

#### Consistent ring

The following table shows a collection of consistent ring meters used to monitor the Orleans consistent ring layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-consistent-ring-size` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the consistent ring size. |
| `orleans-consistent-ring-range-percentage-local` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the consistent ring local percentage. |
| `orleans-consistent-ring-range-percentage-average` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the consistent ring average percentage. |

#### Watchdog

The following table shows a collection of watchdog meters used to monitor the Orleans watchdog layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-watchdog-health-checks` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of watchdog health checks. |
| `orleans-watchdog-health-checks-failed` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of failed watchdog health checks. |

#### Client

The following table shows a collection of client meters used to monitor the Orleans client layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-client-connected-gateways` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the number of connected gateway clients. |

#### Miscellaneous

The following table shows a collection of miscellaneous meters used to monitor various layers.

| Meter name | Type | Description |
|--|--|--|
| `orleans-grains` | <xref:System.Diagnostics.Metrics.Counter`1> | A count representing the number of grains. |
| `orleans-system-targets` | <xref:System.Diagnostics.Metrics.Counter`1> | A count representing the number of system targets. |

#### App requests

The following table shows a collection of app request meters used to monitor the Orleans app request layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-app-requests-latency` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing app request latency. |
| `orleans-app-requests-timedout` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> |  An observable counter representing app requests that have timed out. |

#### Reminders

The following table shows a collection of reminder meters used to monitor the Orleans reminder layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-reminders-tardiness` | <xref:System.Diagnostics.Metrics.Histogram`1> | A histogram representing the number of seconds a reminder is tardy. |
| `orleans-reminders-active` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the number active reminders. |
| `orleans-reminders-ticks-delivered` | <xref:System.Diagnostics.Metrics.Counter`1> | A count representing the number of reminder ticks that have been delivered. |

#### Storage

The following table shows a collection of storage meters used to monitor the Orleans storage layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-storage-read-errors` | <xref:System.Diagnostics.Metrics.Counter`1> |  A count representing the number of storage read errors. |
| `orleans-storage-write-errors` | <xref:System.Diagnostics.Metrics.Counter`1> |  A count representing the number of storage write errors. |
| `orleans-storage-clear-errors` | <xref:System.Diagnostics.Metrics.Counter`1> |  A count representing the number of storage clear errors. |
| `orleans-storage-read-latency` | <xref:System.Diagnostics.Metrics.Histogram`1> | A histogram representing the storage read latency in milliseconds. |
| `orleans-storage-write-latency` | <xref:System.Diagnostics.Metrics.Histogram`1> | A histogram representing the storage write latency in milliseconds. |
| `orleans-storage-clear-latency` | <xref:System.Diagnostics.Metrics.Histogram`1> | A histogram representing the storage clear latency in milliseconds. |

#### Streams

The following table shows a collection of stream meters used to monitor the Orleans stream layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-streams-pubsub-producers-added` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of streaming pubsub producers added. |
| `orleans-streams-pubsub-producers-removed` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of streaming pubsub producers removed. |
| `orleans-streams-pubsub-producers` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of streaming pubsub producers. |
| `orleans-streams-pubsub-consumers-added` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of streaming pubsub consumers added. |
| `orleans-streams-pubsub-consumers-removed` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of streaming pubsub consumers removed. |
| `orleans-streams-pubsub-consumers` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of streaming pubsub consumers. |
| `orleans-streams-persistent-stream-pulling-agents` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the number of persistent stream pulling agents. |
| `orleans-streams-persistent-stream-messages-read` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of persistent stream messages read. |
| `orleans-streams-persistent-stream-messages-sent` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of persistent stream messages sent. |
| `orleans-streams-persistent-stream-pubsub-cache-size` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the persistent stream pubsub cache size. |
| `orleans-streams-queue-initialization-failures` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue initialization failures. |
| `orleans-streams-queue-initialization-duration` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue initialization occurrences. |
| `orleans-streams-queue-initialization-exceptions` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue initialization exceptions. |
| `orleans-streams-queue-read-failures` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue read failures. |
| `orleans-streams-queue-read-duration` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue read occurrences. |
| `orleans-streams-queue-read-exceptions` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue read exceptions. |
| `orleans-streams-queue-shutdown-failures` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue shutdown failures. |
| `orleans-streams-queue-shutdown-duration` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue shutdown occurrences. |
| `orleans-streams-queue-shutdown-exceptions` | <xref:System.Diagnostics.Metrics.Counter`1> | A count of steam queue shutdown exceptions. |
| `orleans-streams-queue-messages-received` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of stream queue messages received. |
| `orleans-streams-queue-oldest-message-enqueue-age` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the age of the oldest enqueued message. |
| `orleans-streams-queue-newest-message-enqueue-age` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the age of the newest enqueued message. |
| `orleans-streams-block-pool-total-memory` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream block pool total memory in bytes. |
| `orleans-streams-block-pool-available-memory` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream block pool available memory in bytes. |
| `orleans-streams-block-pool-claimed-memory` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream block pool claimed memory in bytes. |
| `orleans-streams-block-pool-released-memory` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream block pool released memory in bytes. |
| `orleans-streams-block-pool-allocated-memory` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream block pool allocated memory in bytes. |
| `orleans-streams-queue-cache-size` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream queue cache size in bytes. |
| `orleans-streams-queue-cache-length` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream queue length. |
| `orleans-streams-queue-cache-messages-added` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream queue messages added. |
| `orleans-streams-queue-cache-messages-purged` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream queue messages purged. |
| `orleans-streams-queue-cache-memory-allocated` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream queue memory allocated. |
| `orleans-streams-queue-cache-memory-released` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream queue memory released. |
| `orleans-streams-queue-cache-oldest-to-newest-duration` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the duration from the oldest to the newest stream queue cache. |
| `orleans-streams-queue-cache-oldest-age` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the age of the oldest cached message. |
| `orleans-streams-queue-cache-pressure` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing the pressure on the stream queue cache. |
| `orleans-streams-queue-cache-under-pressure` | <xref:System.Diagnostics.Metrics.ObservableGauge`1> | An observable gauge representing whether the stream queue cache is under pressure. |
| `orleans-streams-queue-cache-pressure-contribution-count` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the stream queue cache pressure contributions. |

#### Transactions

The following table shows a collection of transaction meters used to monitor the Orleans transaction layer.

| Meter name | Type | Description |
|--|--|--|
| `orleans-transactions-started` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of started transactions. |
| `orleans-transactions-successful` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of successful transactions. |
| `orleans-transactions-failed` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of failed transactions. |
| `orleans-transactions-throttled` | <xref:System.Diagnostics.Metrics.ObservableCounter`1> | An observable counter representing the number of throttled transactions. |

### Export metrics

Various third-party metrics providers are available for use with Orleans. Export metrics from your app using the [OpenTelemetry Protocol (OTLP)](https://opentelemetry.io/docs/specs/otlp/). Many observability platforms consume OTLP data directly or through an OpenTelemetry Collector, including [Prometheus](https://prometheus.io), Grafana, and Azure Monitor.

To export metrics using OTLP with Orleans, install the [OpenTelemetry.Exporter.OpenTelemetryProtocol](https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol/) NuGet package and call the following <xref:Microsoft.Extensions.DependencyInjection.IServiceCollection> extension method:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddOtlpExporter()
            .AddMeter("Microsoft.Orleans");
    });
```

The `AddOtlpExporter` method ensures the OTLP exporter is added to the `builder`. Orleans uses a <xref:System.Diagnostics.Metrics.Meter> named `"Microsoft.Orleans"` to create <xref:System.Diagnostics.Metrics.Counter`1> instances for many Orleans-specific metrics. Use the `AddMeter` method to specify the name of the meter to subscribe to, in this case, `"Microsoft.Orleans"`.

You can configure the OTLP exporter endpoint and other options as needed. For example:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri("http://localhost:4317");
            })
            .AddMeter("Microsoft.Orleans");
    });
```

> [!NOTE]
> The default OTLP exporter configuration uses gRPC (typically port `4317`). To export metrics to Prometheus, send telemetry through an OpenTelemetry Collector or configure OTLP/HTTP settings instead.

## Distributed tracing

Distributed tracing is a set of tools and practices for monitoring and troubleshooting distributed applications. It's a key component of observability and a critical tool for understanding your app's behavior. Orleans supports distributed tracing with [OpenTelemetry](https://opentelemetry.io).

Regardless of the distributed tracing exporter you choose, call:

- <xref:Orleans.Hosting.CoreHostingExtensions.AddActivityPropagation(Orleans.Hosting.ISiloBuilder)>: which enables distributed tracing for the silo.
- <xref:Orleans.Hosting.ClientBuilderExtensions.AddActivityPropagation(Orleans.Hosting.IClientBuilder)>: which enables distributed tracing for the client.

Or set the `EnableDistributedTracing` config option to `true`.

Referring back to the [Orleans GPS Tracker sample app](/samples/dotnet/samples/orleans-gps-device-tracker-sample), you can export distributed traces using the [OpenTelemetry Protocol (OTLP)](https://opentelemetry.io/docs/specs/otlp/). To use OpenTelemetry with OTLP and Orleans, install the [OpenTelemetry.Exporter.OpenTelemetryProtocol](https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol/) NuGet package and call the following <xref:Microsoft.Extensions.DependencyInjection.IServiceCollection> extension method in _Program.cs_:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        // Set a service name
        tracing.SetResourceBuilder(
            ResourceBuilder.CreateDefault()
                .AddService(serviceName: "GPSTracker", serviceVersion: "1.0"));

        // Good baseline for general Orleans observability
        tracing.AddSource(Orleans.Diagnostics.ActivitySources.ApplicationGrainActivitySourceName);
        tracing.AddSource(Orleans.Diagnostics.ActivitySources.LifecycleActivitySourceName);

        /*
        // Other source also available
        // Persistence spans
        tracing.AddSource(Orleans.Diagnostics.ActivitySources.StorageActivitySourceName);
        // Internal Runtime spans
        tracing.AddSource(Orleans.Diagnostics.ActivitySources.RuntimeActivitySourceName);
        */

        /*
        // Optionally add all Microsoft.Orleans.* Sources at once
        tracing.AddSource(Orleans.Diagnostics.ActivitySources.AllActivitySourceName);
        */

        tracing.AddOtlpExporter(otlp =>
        {
            otlp.Endpoint = new Uri("http://localhost:4317");
        });
    });
```

The OTLP exporter works with many observability backends including Jaeger, Zipkin, Grafana Tempo, and Azure Monitor. The traces can be visualized in tools like Jaeger UI:

:::image type="content" source="../media/jaeger-ui.png" lightbox="../media/jaeger-ui.png" alt-text="Orleans GPS Tracker sample app: Jaeger UI trace.":::

For more information, see [Distributed tracing](../../../core/diagnostics/distributed-tracing.md).

:::zone-end

:::zone target="docs" pivot="orleans-3-x"

Orleans outputs its runtime statistics and metrics through the <xref:Orleans.Runtime.ITelemetryConsumer> interface. Your application can register one or more telemetry consumers for its silos and clients to receive statistics and metrics the Orleans runtime periodically publishes. These can be consumers for popular telemetry analytics solutions or custom ones for any other destination and purpose. Three telemetry consumers are currently included in the Orleans codebase.

They're released as separate NuGet packages:

- `Microsoft.Orleans.OrleansTelemetryConsumers.AI`: For publishing to [Azure Application Insights](/azure/azure-monitor/app/app-insights-overview).

- `Microsoft.Orleans.OrleansTelemetryConsumers.Counters`: For publishing to Windows performance counters. The Orleans runtime continually updates many of them. The _CounterControl.exe_ tool, included in the [`Microsoft.Orleans.CounterControl`](https://www.nuget.org/packages/Microsoft.Orleans.CounterControl/) NuGet package, helps register necessary performance counter categories. It must run with elevated privileges. Monitor the performance counters using any standard monitoring tools.

- `Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic`: For publishing to [New Relic](https://newrelic.com/).

To configure your silo and client to use telemetry consumers, the silo configuration code looks like this:

```csharp
var siloHostBuilder = new HostBuilder()
    .UseOrleans(c =>
    {
        c.AddApplicationInsightsTelemetryConsumer("INSTRUMENTATION_KEY");
    });
```

The client configuration code looks like this:

```csharp
var clientBuilder = new ClientBuilder();
clientBuilder.AddApplicationInsightsTelemetryConsumer("INSTRUMENTATION_KEY");
```

To use a custom-defined <xref:Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration> (which might have <xref:Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration.TelemetryProcessors>, <xref:Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration.TelemetrySinks>, etc.), the silo configuration code looks like this:

```csharp
var telemetryConfiguration = TelemetryConfiguration.CreateDefault();
var siloHostBuilder = new HostBuilder()
    .UseOrleans(c =>
    {
        c.AddApplicationInsightsTelemetryConsumer(telemetryConfiguration);
    });
```

Client configuration code look like this:

```csharp
var clientBuilder = new ClientBuilder();
var telemetryConfiguration = TelemetryConfiguration.CreateDefault();
clientBuilder.AddApplicationInsightsTelemetryConsumer(telemetryConfiguration);
```

:::zone-end

## See also

- [Logging in .NET](../../../core/extensions/logging/overview.md)
- [.NET metrics](../../../core/diagnostics/metrics.md)
- [Investigate performance counters (dotnet-counters)](../../../core/diagnostics/dotnet-counters.md)
- [.NET distributed tracing](../../../core/diagnostics/distributed-tracing.md)
