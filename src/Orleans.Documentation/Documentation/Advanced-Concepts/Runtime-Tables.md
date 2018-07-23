---
layout: page
title: Azure Runtime Tables
---

# Azure Runtime Tables

Orleans maintains a number of internal tables for different runtime mechanisms. Here we list all the tables as they are organized if Azure is used as the system store, and provide more details on their internal structure.
When SQL, ZooKeeper, Consul and other technologies are used for system store, organization and exact structure of the data may vary, but the general idea stays the same. 

Runtime tables:

1. Orleans Silo Instances table
2. Reminders table
3. Silo Metrics table
4. Clients Metrics table
5. Silo Statistics table
6. Clients Statistics table

## Orleans Silo Instances table

Orleans Silo Instances table, also commonly referred to as Membership table, lists the set of silos that make an Orleans deployment. More details can be found in the description of the [Cluster Management Protocol](Cluster-Management.md) that maintains this table.

All rows in this table consist of the following columns ([`SiloInstanceTableEntry`](https://github.com/dotnet/orleans/blob/master/src/OrleansAzureUtils/Storage/OrleansSiloInstanceManager.cs#L16)):

1. *PartitionKey* - deployment id.
2. *RowKey* - Silo IP Address + "-" + Silo Port + "-" + Silo Generation number (epoch)
3. *DeploymentId* - the deployment id of this Orleans service
4. *Address* - IP address
5. *Port* - silo to silo TCP port
6. *Generation* - Generation number (epoch number)
7. *HostName* - silo Hostname
8. *Status* - status of this silo, as set by cluster management protocol. Any of the type [`Orleans.Runtime.SiloStatus`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Runtime/SiloStatus.cs)
9. *ProxyPort* - silo to clients TCP port
10. *Primary* - whether this silo is primary or not. Deprecated.
11. *RoleName* - If running in Azure - the name of this role. If running on premises, the name of the executing assembly.
12. *InstanceName* - If running in Azure - the name of this role instance. If running on premises, the silo name that the silo host gave it.
13. *UpdateZone* - Azure update zone, if running in Azure.
14. *FaultZone* - Azure fault zone, if running in Azure.
15. *SuspectingSilos* - the list of silos that suspect this silo. Managed by cluster management protocol.
16. *SuspectingTimes* - the list of times when this silo was suspected. Managed by cluster management protocol.
17. *StartTime* - the time when this silo was started.
18. *IAmAliveTime* - the last time this silo reported that it is alive. Used for diagnostics and troubleshooting only.

There is also a special row in this table, called membership version row, with the following columns:

1. *PartitionKey* - deployment id.
2. *RowKey* - "VersionRow" constant string
3. *DeploymentId*
4. *MembershipVersion* - the latest version of the current membership configuration.

### Naming:
The silo instance row has 3 names: hostname, rolename and instance name. What is the difference?
First, it is important to note that [Orleans cluster protocol](Cluster-Management.md) does not use any of these names for distinguishing between silos. Instead it uses `IP:port:epoch` as a unique identity of a silo instance. Therefore, setting of those 3 names has no impact on runtime correctness. It is in the table merely to help diagnostics and operational troubleshooting.

Hostname is always set to the name of this host, as returned by `Dns.GetHostName()`.
Role name is a logical name of the whole service and instance name is the name of this specific silo instance within this service.

Role name and Instance name depend on the hosting - where the silo runs. Each silo host can set those differently.
Azure host (`AzureSiloHost`) sets the role name to Azure role name (`myRoleInstance.Role.Name`) and instance name to Azure role Instance name (`myRoleInstance.Id`).
On premises (`SiloHost`) the role name is the executing  assembly name (`Assembly.GetExecutingAssembly().GetName().Name`) and the instance name is the name the host gave to that silo when it was started.


## Orleans Reminders table

Orleans Reminders table durably stores all the reminders registered in the system. Each reminder has a separate row. All rows in this table consist of the following columns ([`ReminderTableEntry`](https://github.com/dotnet/orleans/blob/master/src/OrleansAzureUtils/Storage/RemindersTableManager.cs#L14)):

1. *PartitionKey* - ServiceId + "_" + GrainRefConsistentHash
2. *RowKey* -  GrainReference + "-" ReminderName
3. *GrainReference* - the grain reference of the grain that created this reminder.
4. *ReminderName* - the name of this reminder
5. *ServiceId* - the service id of the currently running Orleans service
6. *DeploymentId* - the deployment id of the currently running Orleans service
7. *StartAt* - the time when this reminder was supposed to tick in the first time
8. *Period* - the time period for this reminder
9. *GrainRefConsistentHash* - the consistent hash of the GrainReference


## Silo Metrics table

Silo metrics table contains a small set of per-silo important key performance metrics (usually known as KPI - [Key Performance Indicators](https://en.wikipedia.org/wiki/Performance_indicator)). Each silo has one row, periodically updated in-place by its silo ([`SiloMetricsData`](https://github.com/dotnet/orleans/blob/master/src/OrleansAzureUtils/Storage/SiloMetricsTableDataManager.cs#L12)).

1. *PartitionKey* - DeploymentId
2. *RowKey* -  silo name
3. *DeploymentId* -  the deployment id of this Orleans service
4. *Address* - the silo address (ip:port:epoch) of this silo
5. *SiloName* - the name of this silo (in Azure it is its Instance name)
6. *GatewayAddress* - the gateway ip:port of tis silo
7. *HostName* - the hostname of this silo
8. *CPU* - current CPU utilization
9. *MemoryUsage* - current memory usage (`GC.GetTotalMemory(false)`)
10. *Activations* - number of activations on this silo
11. *RecentlyUsedActivations* - number of activations on this silo that were used in the last 10 minutes (Note: this number may currently not be accurate if  different age limits are used for different grain types).
12. *SendQueue* - the current size of the send queue (number of messages waiting to be send). Only captures remote messages to other silos (not including messages to the clients).
13. *ReceiveQueue* - the current size of the receive queue (number of messages that arrived to this silo and are waiting to be dispatched). Captures both remote and local messages from other silos as well as from the clients.
14. *RequestQueue*
15. *SentMessages* - total number of remote messages sent to other silos as well as to the clients.
16. *ReceivedMessages* - total number of remote received messages, from other silos as well as from the clients.
17. *LoadShedding* - whether this silo is currently overloaded and is in the load shedding mode.
18. *Clients* - number of currently connected clients


## Clients Metrics table

Silo metrics table containes a small set of per-Orleans-client important key performance metrics. Each client has one row,  periodically updated in-place by its client. Client metrics are essentilay a subset of silo metrics ([`ClientMetricsData `](https://github.com/dotnet/orleans/blob/master/src/OrleansAzureUtils/Storage/ClientMetricsTableDataManager.cs#L14)).

1. *PartitionKey* - DeploymentId
2. *RowKey* - Address
3. *DeploymentId* -  the deployment id of this Orleans service
4. *Address* - the address (ip:port) of this client
5. *ClientId* - the unique name of this client (pseudo client grain id)
6. *HostName* - the hostname of this client
7. *CPU* - current CPU utilization
8. *MemoryUsage* - current memory usage (`GC.GetTotalMemory(false)`)
9. *SendQueue* - the current size of the send queue (number of messages waiting to be send). Captures remote messages to other silos.
10. *ReceiveQueue* - the current size of the receive queue (number of messages that arrived to this client and are waiting to be dispatched, including responses).
11. *SentMessages* - total number of remote messages sent to silos.
12. *ReceivedMessages* - total number of remote received messages from silos.
13. *ConnectedGatewayCount* - number of gateways that this client is currently connected to.


## Silo Statistics table

Silo Statistics table containes a large set of per-silo detailed statistic counters. Most of them are low level performance statistics, which are usualy used in troubleshooting scenarios. A set of examples and methodology of how those statistics can be used are described in our paper [PAD: Performance Anomaly Detection in Multi-Server Distributed Systems and a proof of concept](https://www.microsoft.com/en-us/research/publication/pad-performance-anomaly-detection-in-multi-server-distributed-systems).
Each statistic value is one row. This table is append only. Every silo periodicaly (usually every 5 minutes, configurable) appends all its latest statistic counter values. The number of counters per silo is currently about 200. So every 5 minutes every silo appends around 200 rows to this table.

Each row is in the format ([`StatsTableData`](https://github.com/dotnet/orleans/blob/master/src/OrleansAzureUtils/Storage/StatsTableDataManager.cs#L14)):

1. *PartitionKey* - `DeploymentId$ReverseTimestampToTheNearestHour` - deploymentId and last hour
2. *RowKey* - `ReverseTimestampToTheNearestSecond$Name$counter` - current second, silo name, monotonically growing sequence number
3. *DeploymentId* -  the deployment id of this Orleans service
4. *Time* -  the current time of the reporting on the reporting silo
5. *Address* - the silo address (ip:port:epoch) of this silo
6. *Name* - the name of this silo (in Azure it is its Instance name)
7. *HostName* - the host name of this silo
8. *Statistic* - the name of the statistic counter
9. *StatValue* - the value of the statistic counter
10. *IsDelta* - if this the statistic counter value is delta since last value reported or an absolute value

The rationale behind the choice of partition key and row key is described [here](https://github.com/dotnet/orleans/blob/master/src/Orleans/AzureUtils/StatsTableDataManager.cs#L149) and [here](https://github.com/dotnet/orleans/issues/34).

## Clients Statistics table

Same as Silo Statistics table but for client.
