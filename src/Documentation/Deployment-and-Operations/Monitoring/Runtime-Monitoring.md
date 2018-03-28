---
layout: page
title: Runtime Monitoring
---

# Runtime Monitoring

Orleans output its runtime statistics and metrics through `ITelemetryConsumer`. Application can register telemetry consumer with their cluster, and then orleans runtime will periotically publish statistics and metrics to the consumers. Application developpers can implement their own `ITelemetryConsumer` on top of the monitoring service they choose, or they can utilize the telemetry consumer we support. We currently support three types of telemetry consumer which are released through their own nuget: 
- `Microsoft.Orleans.OrleansTelemetryConsumers.AI`, which would publish to [Application Insights](https://azure.microsoft.com/en-us/services/application-insights/).
- `Microsoft.Orleans.OrleansTelemetryConsumers.Counters`, whcih would publish to Windows performance counters. The Orleans runtime continually updates a number of them. CounterControl.exe, which can be find in `Microsoft.Orleans.CounterControl` nuget, helps register the counters, and needs to run with elevated privileges. Also, the performance counters can be monitored using any of the standard monitoring tools.
- `Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic`, which would publish to [New Relic](https://newrelic.com/).

To configure your server and client to use telemetry consumer, server side configuration would look like this : 
```c#
var siloHostBuilder = new SiloHostBuilder();
//configure the silo with AITelemetryConsumer
siloHostBuilder.Configure<TelemetryOptions>(options => options.AddConsumer<AITelemetryConsumer>);
```

client side configuration would look like this : 
```c#
var clientBuilder = newClientBuilder();
//configure the clientBuilder with AITelemetryConsumer
clientBuilder.Configure<TelemetryOptions>(options => options.AddConsumer<AITelemetryConsumer>);
```

**Watching error codes in MDS** - Orleans automatically writes different error messages into logger. This logger can be configured to output its data to various destinations. For example, the Halo team redirects all logs in production to MDS. They have written custom alerts in MDS to watch for specific error codes and count their occurrences, and alert them when those reach a certain threshold. The list of important error codes to watch is specified here:

* [Silo Error Code Monitoring](Silo-Error-Code-Monitoring.md)

* [Client Error Code Monitoring](Client-Error-Code-Monitoring.md)