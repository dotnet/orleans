---
title: Orleans observability
description: Configure OpenTelemetry logging, metrics, and tracing for Orleans.
ms.date: 08/12/2026
ms.topic: overview
---

# Orleans observability

Orleans uses standard .NET observability APIs:

- [Microsoft.Extensions.Logging](https://learn.microsoft.com/dotnet/core/extensions/logging/overview) for structured logs.
- A <xref:System.Diagnostics.Metrics.Meter> named `Microsoft.Orleans` for [.NET metrics](https://learn.microsoft.com/dotnet/core/diagnostics/metrics).
- Several <xref:System.Diagnostics.ActivitySource> instances for [.NET distributed tracing](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing).

These signals are complementary. Metrics detect a change, traces locate it in a request path, and logs explain discrete events or failures. The [Orleans Dashboard](../../dashboard/index.md) is useful for interactive inspection, but an external telemetry backend is the durable source for alerting, retention, and cross-service correlation.

## Configure OpenTelemetry once

[OpenTelemetry](https://opentelemetry.io/docs/) provides a vendor-neutral pipeline for logs, metrics, and traces. Install these packages in the host which runs Orleans:

- [OpenTelemetry.Extensions.Hosting](https://www.nuget.org/packages/OpenTelemetry.Extensions.Hosting)
- [OpenTelemetry.Exporter.OpenTelemetryProtocol](https://www.nuget.org/packages/OpenTelemetry.Exporter.OpenTelemetryProtocol)
- Instrumentation packages for the surrounding application, such as `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, and `OpenTelemetry.Instrumentation.Runtime`

The following configuration works for a silo. Register <xref:Orleans.Hosting.CoreHostingExtensions.AddActivityPropagation*?displayProperty=nameWithType> on silos and <xref:Orleans.Hosting.ClientBuilderExtensions.AddActivityPropagation*?displayProperty=nameWithType> on Orleans clients.

:::code language="csharp" source="./snippets/observability/Program.cs" id="OpenTelemetry":::

Set `OTEL_EXPORTER_OTLP_ENDPOINT` to the [OpenTelemetry Protocol (OTLP)](https://opentelemetry.io/docs/specs/otlp/) endpoint, for example `http://localhost:4317`. The OpenTelemetry .NET SDK honors standard `OTEL_*` environment variables. In a .NET Aspire application, the AppHost supplies the OTLP endpoint to referenced projects, so this configuration sends telemetry to the Aspire dashboard without an Orleans-specific exporter.

Use an [OpenTelemetry Collector](https://opentelemetry.io/docs/collector/) between applications and the final backend when you need buffering, routing, redaction, or backend-specific authentication.

## Meter versus ActivitySource

`AddMeter("Microsoft.Orleans")` subscribes to numeric Orleans instruments. It doesn't enable traces. Use metrics for rates, counts, queue pressure, latency distributions, and alerts.

`AddSource(...)` subscribes to spans. It doesn't collect Orleans metrics. Orleans exposes these source names through <xref:Orleans.Diagnostics.ActivitySources>:

| Source | Scope |
|---|---|
| `Microsoft.Orleans.Application` | Application grain calls |
| `Microsoft.Orleans.Runtime` | Runtime operations |
| `Microsoft.Orleans.Lifecycle` | Activation, migration, and deactivation |
| `Microsoft.Orleans.Storage` | Grain storage operations |
| `Microsoft.Orleans.DurableJobs` | Durable job scheduling and execution |
| `Microsoft.Orleans.*` | Wildcard for all Orleans activity sources |

The example selects the first four sources and the separate
`Microsoft.Orleans.Reminders` source explicitly so the collected scope is
visible in code. Add durable jobs when used. A wildcard is convenient during
investigation but can begin collecting new sources after an Orleans upgrade, so
review its volume and data policy.

## Propagate trace context through grain calls

Register `AddActivityPropagation()` on every silo and Orleans client which participates in a trace. It installs grain-call filters which carry the current W3C trace context and baggage through Orleans messages. Without it, Orleans spans can still be exported, but an incoming HTTP span and the resulting grain-call spans won't form one distributed trace.

Treat baggage as transmitted application data. Don't put secrets, credentials, personal data, or unbounded user input in baggage. Prefer a small set of approved correlation fields.

## Set resource identity

Every process should set stable resource attributes:

- `service.name`: the logical service, such as `orders-silo`.
- `service.version`: the deployed application version.
- `service.instance.id`: a unique process or pod identifier.
- `deployment.environment.name`: the environment.

Don't use a silo address as `service.name`; put changing instance identity in `service.instance.id`. Configure cluster, region, or tenant attributes only when their value set is bounded and permitted by your telemetry data policy.

## Control cost and volume

- Export metrics continuously at an interval appropriate for your alerting objectives.
- Use parent-based trace sampling so a request keeps one sampling decision across HTTP, Orleans, and downstream calls.
- Keep errors and slow requests with a tail-sampling collector when the backend supports it.
- Increase sampling temporarily during an incident instead of running full-fidelity tracing indefinitely.
- Keep metric dimensions bounded. Grain IDs, request IDs, user IDs, and exception messages are unsuitable metric attributes.
- Filter noisy log categories at the provider and avoid logging full grain state or message payloads.

The example's 10% head-sampling ratio is a starting point, not a universal production value. Select it from traffic volume, retention cost, and the probability of capturing rare failures.

## Next steps

- [Monitor Orleans metrics](metrics.md)
- [Interpret Orleans signals](signals.md)
- [Troubleshoot Orleans incidents](troubleshooting.md)
- [Secure and operate the Orleans Dashboard](../../dashboard/index.md)
