---
layout: page
title: Runtime Monitoring
---

[!include[](../../../warning-banner.md)]

# Runtime Monitoring

[[THIS IS IN NEED OF REVIEW]]

There are five ways Orleans deployment can be monitored by an external operator by utilizing the data that Orleans writes automatically to Azure storage.

The tables mentioned below are desribed in more detail [here](../../Runtime-Implementation-Details/Runtime-Tables.md).

**OrleansSilosTable for cluster membership** - this table lists all silos in the deployment (partition key DeploymentID, row key silo id). The operator can use this table to check cluster health, watch the current set of live silos, or learn why and when a certain silo went down. Orleans' cluster membership protocol uses this table internally and updates it with significant membership events (silos goes up and down).

**OrleansSiloMetrics** table for coarse grain performance statistics - Orleans writes a small number (about 10) of coarse-grain performance stats into this table (partition key DeplomentID, row key silo id). The table is updated automatically every X seconds (configurable) for each silo. The metrics include silo CPU, memory usage, number of grain activations on this silo, number of messages in the send/receive queue, etc. This data can be used to compare silos, check that there are no significant outliers (for example, one silo runs at much higher CPU), or simply check that in general the metrics reported by silos are in the expected range. In addition, this data can be used to decide to add new silos if the system becomes overloaded or reduce the number of silos if the system is mostly idle.

**OrleansSiloStatistics** table - this table includes a much larger number of performance statistics (hundreds of counters) which provide a much more detailed and in-depth view of the internal silo state. This table is currently not recommended for use by external operators. It is mainly for Orleans developers to help them troubleshoot complex production problems, if they occur. The Orleans team is building tools to analyze this data automatically and provide compact recommendations to operators based on it. Such tools can also be built by anyone independently.

**Watching error codes in MDS** - Orleans automatically writes different error messages into logger. This logger can be configured to output its data to various destinations. For example, the Halo team redirects all logs in production to MDS. They have written custom alerts in MDS to watch for specific error codes and count their occurrences, and alert them when those reach a certain threshold. The list of important error codes to watch is specified here:

* [Silo Error Code Monitoring](Silo-Error-Code-Monitoring.md)

* [Client Error Code Monitoring](Client-Error-Code-Monitoring.md)

**Windows performance counters** - The Orleans runtime continually updates a number of them. CounterControl.exe helps register the counters, and needs to run with elevated privileges. Obviously, the performance counters can be monitored using any of the standard monitoring tools.
