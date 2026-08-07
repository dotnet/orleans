---
title: Orleans Dashboard
description: Securely deploy and operate the Orleans Dashboard for live cluster inspection.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans Dashboard

The Orleans Dashboard provides live inspection of silos, grain activations, calls, reminders, runtime counters, logs, and grain state. It is an administrative surface, not a public application endpoint or a replacement for retained OpenTelemetry data.

## Secure the dashboard before exposing it

> [!WARNING]
> <xref:Orleans.Dashboard.ServiceCollectionExtensions.MapOrleansDashboard*?displayProperty=nameWithType> doesn't require authentication by default. The dashboard can expose topology, grain type and key information, runtime properties, method activity, log messages, reminder metadata, and serialized grain state. Never expose it to the public internet or an untrusted application network without authentication, authorization, and transport security.

Use defense in depth:

1. **Authenticate operators.** Integrate the ASP.NET Core host with your organization's identity provider. Don't rely on a hard-to-guess route.
2. **Authorize the entire route group.** Apply a policy to the <xref:Microsoft.AspNetCore.Routing.RouteGroupBuilder> returned by `MapOrleansDashboard()`.
3. **Restrict the network path.** Bind an administrative listener or put the route behind a private ingress, VPN, firewall, or zero-trust access proxy. A route prefix isn't a security boundary.
4. **Use HTTPS.** Protect credentials, cookies, dashboard responses, and streamed logs in transit.
5. **Limit sensitive data.** Grain state and logs can contain secrets or personal data. Apply normal data classification, retention, and access-audit requirements.
6. **Disable features you don't need.** Set `HideTrace = true` when live log streaming isn't required. Avoid exposing grain-state inspection to operators who don't need it.

The following example assumes the application has a cookie sign-in flow backed by its identity provider. It protects every dashboard asset and API endpoint with one policy:

:::code language="csharp" source="./snippets/secure-dashboard/Program.cs" id="SecureDashboard":::

Validate the authorization behavior with an unauthenticated request and with users inside and outside the operator role. Also check proxy forwarding and route-prefix behavior in the deployed topology.

## Install and map the dashboard

Add [Microsoft.Orleans.Dashboard](https://www.nuget.org/packages/Microsoft.Orleans.Dashboard) to the web host. [Microsoft.Orleans.Dashboard.Abstractions](https://www.nuget.org/packages/Microsoft.Orleans.Dashboard.Abstractions) contains shared types such as <xref:Orleans.Dashboard.NoProfilingAttribute> and is brought in by the main package.

Call <xref:Orleans.Dashboard.ServiceCollectionExtensions.AddDashboard*?displayProperty=nameWithType> on the silo builder and map the route after building the ASP.NET Core app. A route prefix is recommended to avoid claiming the web application's root:

```csharp
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddDashboard();
});

var app = builder.Build();

app.MapOrleansDashboard("/dashboard")
    .RequireAuthorization();
```

The route group includes static dashboard assets and its backing APIs. Apply authentication, authorization, rate limits, headers, and other endpoint conventions to the returned group rather than protecting only the HTML page. Use the policy-based configuration in the preceding secure dashboard example when dashboard access is limited to an operator role.

## Configure collection

```csharp
siloBuilder.AddDashboard(options =>
{
    options.HideTrace = true;
    options.CounterUpdateIntervalMs = 2_000;
    options.HistoryLength = 100;
});
```

| Option | Default | Operational effect |
|---|---:|---|
| <xref:Orleans.Dashboard.DashboardOptions.HideTrace> | `false` | Disables the live log-streaming endpoint when `true`. |
| <xref:Orleans.Dashboard.DashboardOptions.CounterUpdateIntervalMs> | `1000` | Sets the counter sampling interval in milliseconds; the minimum is 1000. |
| <xref:Orleans.Dashboard.DashboardOptions.HistoryLength> | `100` | Controls retained in-memory dashboard history. Larger values consume more memory. |

The dashboard registers a logging provider and collects runtime metrics for display. Method profiling adds an incoming grain-call filter. By default, profiling becomes inactive after one minute without dashboard queries. Continuous profiling is available but has ongoing overhead:

```csharp
builder.Services.Configure<GrainProfilerOptions>(options =>
{
    options.TraceAlways = true;
    options.DeactivationTime = TimeSpan.FromMinutes(5);
});
```

Leave <xref:Orleans.Dashboard.GrainProfilerOptions.TraceAlways> disabled unless continuous method statistics justify the cost. Load test representative traffic with the dashboard configuration you plan to deploy. Use <xref:Orleans.Dashboard.NoProfilingAttribute> on a grain class or method only when omitting it from dashboard method statistics is acceptable.

## Choose a deployment boundary

### Co-host with a silo

Co-hosting is the simplest setup and gives the dashboard access to local silo services. Put its HTTP route on an administrative network boundary distinct from public application routes. Adding the dashboard to a silo doesn't itself open an HTTP listener; `MapOrleansDashboard()` maps endpoints on the ASP.NET Core host.

### Use a separate Orleans client host

An Orleans client can host the web UI:

```csharp
dashboardBuilder.UseOrleansClient(clientBuilder =>
{
    clientBuilder
        .UseStaticClustering(options => options.Gateways.Add(gatewayAddress))
        .AddDashboard();
});
```

Every silo must still call `AddDashboard()` so cluster data and profiling are available. Protect the client host's route and its network access to gateways. A separate host reduces direct HTTP exposure on silos, but it doesn't make the dashboard data non-sensitive.

## Operate it in production

- Use the dashboard for short-lived interactive diagnosis and OpenTelemetry for durable metrics, traces, logs, alerts, and retention.
- Monitor dashboard request volume, process CPU/memory, and grain-call latency after enabling profiling.
- Keep dashboard and Orleans package versions aligned.
- Audit operator access using the surrounding authentication/proxy platform.
- Don't paste grain state or live logs into tickets or chats without redaction.
- Disable or remove dashboard mapping in environments where no operational access path is approved.

## Troubleshooting

### The dashboard reports lost connectivity

Confirm the dashboard host has an active Orleans client connection, can resolve/reach advertised gateways, and uses the same cluster/service identifiers and clustering provider as the silos. For a separate host, verify `AddDashboard()` is also configured on every silo. Follow the [client connection runbook](../host/monitoring/troubleshooting.md#client-cant-connect).

### Profiling data is empty

Generate calls to the grain method, keep the page active, and confirm neither the class nor method has <xref:Orleans.Dashboard.NoProfilingAttribute>. If `TraceAlways` is `false`, profiling stops after <xref:Orleans.Dashboard.GrainProfilerOptions.DeactivationTime> without dashboard queries.

### Live logs return 403

`HideTrace = true` intentionally disables the trace endpoint. If it is `false`, check the dashboard authorization policy and the authenticated user's claims before changing the option.

## See also

- [Orleans observability](../host/monitoring/index.md)
- [Interpret Orleans observability signals](../host/monitoring/signals.md)
