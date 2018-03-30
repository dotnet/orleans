---
layout: page
title: Runtime Monitoring
---

# Runtime Monitoring

Orleans output its runtime statistics and metrics through the `ITelemetryConsumer` interface.
Application can register one or more telemetry consumers with for their silos and clients, to receives statistics and metrics that Orleans runtime  periotically publishes.
These can be consumers for popular telemetry analytics solutions or custom ones for any other destination and purpose.
Three telemetry consumer are currently included in the Orleans codebase.

They are released as separate NuGet packages: 

- `Microsoft.Orleans.OrleansTelemetryConsumers.AI` for publishing to [Application Insights](https://azure.microsoft.com/en-us/services/application-insights/).

- `Microsoft.Orleans.OrleansTelemetryConsumers.Counters` for publishing to Windows performance counters.
The Orleans runtime continually updates a number of them.
CounterControl.exe tool, included in the [`Microsoft.Orleans.CounterControl`](https://www.nuget.org/packages/Microsoft.Orleans.CounterControl/) NuGet package, helps register necessary performance counter categories.
It has to run with elevated privileges.
The performance counters can be monitored using any of the standard monitoring tools.

- `Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic`, for publishing to [New Relic](https://newrelic.com/).

To configure your silo and client to use telemetry consumers, silo configuration code looks like this: 
```c#
var siloHostBuilder = new SiloHostBuilder();
//configure the silo with AITelemetryConsumer
siloHostBuilder.Configure<TelemetryOptions>(options => options.AddConsumer<AITelemetryConsumer>);
```

client configuration code look like this: 
```c#
var clientBuilder = newClientBuilder();
//configure the clientBuilder with AITelemetryConsumer
clientBuilder.Configure<TelemetryOptions>(options => options.AddConsumer<AITelemetryConsumer>);
```

* [Silo Error Code Monitoring](Silo-Error-Code-Monitoring.md)

* [Client Error Code Monitoring](Client-Error-Code-Monitoring.md)
