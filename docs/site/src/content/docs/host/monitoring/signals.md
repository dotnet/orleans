---
title: Interpret Orleans observability signals
description: Use Orleans logs, metrics, traces, correlation, and alerts without relying on stale catalogs.
ms.date: 08/15/2026
ms.topic: concept-article
---

# Interpret Orleans observability signals

Start with service-level symptoms, then use Orleans telemetry to narrow the cause. Alerting on every runtime event or instrument creates noise and couples operations to implementation details.

## Logs

Orleans writes through [`Microsoft.Extensions.Logging`](https://learn.microsoft.com/dotnet/core/extensions/logging/overview). Preserve the structured fields supplied by the provider, including category, level, event ID, exception, trace ID, and span ID. Configure category levels using normal .NET logging configuration.

An Orleans <xref:Microsoft.Extensions.Logging.EventId> is a diagnostic identifier, not a complete incident definition. Numeric ranges and event assignments can change as the runtime evolves. Instead of maintaining a copied table:

- Query the generated <xref:Orleans.ErrorCode> API reference when investigating a known ID.
- To inspect definitions under active development, consult the [runtime error-code source on the `main` branch](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core.Abstractions/Logging/ErrorCodes.cs).
- Alert on a sustained symptom, exception type, or known event plus service impact, rather than every warning.

Record application correlation fields in structured properties or an approved activity tag. Don't parse rendered log text when a structured field is available.

## Metrics

Subscribe to the `Microsoft.Orleans` meter. Discover the exact instrument set emitted by your deployed version. For installation and command details, see the [`dotnet-counters` diagnostic tool](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters):

```dotnetcli
dotnet-counters monitor -n <ProcessName> --counters Microsoft.Orleans
```

See [Monitor Orleans metrics](metrics.md) for the current client and silo signals, instrument types, units, dimensions, and health interpretation. Counter values usually need a rate or increase over a time window, gauges need a deliberately chosen point-in-time aggregation, and histograms need percentiles plus request volume. Compare each silo with the cluster aggregate: one outlier suggests a host or partition problem, while a cluster-wide shift suggests a shared dependency or traffic change.

### Cardinality

Metric backends create a time series for each unique attribute set. Bound attributes to small values such as operation, direction, status, grain type, provider name, and silo identity. Never add grain key, trace ID, request ID, stack trace, URL query, or arbitrary exception text as a metric attribute.

Estimate the series count before adding a dimension:

`instruments x attribute-value combinations x instances`

Large combinations increase memory, network, and backend cost even when each individual label seems reasonable.

## Traces

Use application spans to follow grain calls and runtime, lifecycle, and storage spans to explain where time was spent. A useful trace should answer:

1. Which entry point initiated the call?
2. Which grain operation was invoked?
3. Was time spent waiting, executing application code, activating, placing, or accessing storage?
4. Which exception or status ended the operation?

If spans appear as separate traces, confirm <xref:Orleans.Hosting.ClientBuilderExtensions.AddActivityPropagation*?displayProperty=nameWithType> is registered on the client and the corresponding silo registration is present on all silos in the path. Also verify that samplers honor the parent decision and that proxies preserve W3C `traceparent`.

Avoid recording grain keys and state values by default. They can be high-cardinality or sensitive. If an incident requires them, use restricted, time-limited capture and remove it afterward.

## Stream consumer cleanup

Subscribe to <xref:Orleans.Streaming.Diagnostics.StreamingEvents.AllEvents*> to correlate a consumer's delivery and cleanup transitions by provider, stream, subscription, consumer endpoint, and silo. <xref:Orleans.Streaming.Diagnostics.StreamingEvents.MessageDeliveryFailed> includes the attempted position and original exception. <xref:Orleans.Streaming.Diagnostics.StreamingEvents.SubscriptionUnregistration> reports when the pulling agent requests pubsub cleanup and when that request completes or fails. <xref:Orleans.Streaming.Diagnostics.StreamingEvents.SubscriptionUnregistered> marks the successful durable removal.

When a client route becomes stale, the runtime refreshes its candidate routes at the owning gateways while preserving the directory's versioned routing state. An available gateway receives the request; an unavailable client produces <xref:Orleans.Runtime.ClientNotAvailableException>, which initiates subscription cleanup. Owner-refresh failures retain the pending refresh so that a subsequent lookup can retry. For a cleanup timeout, capture the last observed transition and its exception: a delivery failure identifies the routing or consumer failure, a pending unregistration identifies the pubsub request, and a failed unregistration identifies its storage or callback failure. Keep diagnostic capture bounded and retain positions and identifiers rather than event payloads.

## Baseline alerts

Tune alerts against normal traffic and service objectives. A practical initial set is:

- Client has no connected gateway for longer than the expected reconnect window.
- Request timeout or error ratio exceeds the service objective over multiple windows.
- P95/P99 request latency is high while request volume is nonzero.
- Rejected, dropped, or load-shed messages increase.
- Storage error rate or latency rises above the provider baseline.
- Long-running turns increase and remain elevated.
- Available memory approaches the configured process/container limit or GC pause time rises.
- Active membership differs from the expected deployment for longer than a rollout or restart.
- Graceful shutdown doesn't complete within the orchestrator termination budget.
- TLS certificates are approaching expiration.

Page on user impact or imminent data/availability risk. Route isolated warnings and capacity trends to tickets or dashboards unless they cross a sustained threshold.

## Dashboard and telemetry backends

The Orleans Dashboard provides a current operational view and method profiling. Use logs, metrics, traces, and alerts for durable telemetry. Secure the dashboard as an administrative endpoint and export telemetry through OTLP. See [Orleans Dashboard](../../dashboard/index.md).
