---
layout: page
title: Runtime Tables
---

Orleans maintains a number of internal tables for different runtime mechanisms. Here we list all the tables and provide more details on their internal structure.

Runtime tables:

1. Orleans Silo Instances table
2. Reminders table
3. Silo Metrics table
4. Clients Metrics table
5. Silo Statistics table
6. Clients Statistics table

## Orleans Silo Instances table

Orleans Silo Instances table, also commonly referred to as Membership table, lists the set of silos that make an Orleans deployment. More details can be found in the description of the [Cluster Management Protocol](Cluster-Management) that maintains this table.

All rows in this table consist of the following columns:

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
11. *RoleName* - The name of this role, if running is Azure.
12. *InstanceName* - The name of this role instance, if running is Azure.
13. *UpdateZone* - Azure update zone, if running is Azure.
14. *FaultZone* - Azure fault zone, if running is Azure.
15. *SuspectingSilos* - the list of silos that suspect this silo. Managed by cluster management protocol. 
16. *SuspectingTimes* - the list of times when this silo was suspected. Managed by cluster management protocol. 
17. *StartTime* - the time when this silo was started.
18. *IAmAliveTime* - the last time this silo reoprted that it is alive. Used for diagnostics and troubleshooting only.

There is also a special row in this table, called membership version row, with the following columns:

1. *PartitionKey* - deployment id.
2. *RowKey* - "VersionRow" costant string
3. *DeploymentId* 
4. *MembershipVersion* - the latest version of the current membership configuration. 

## Orleans Reminders table

Orleans Reminders table durably stores all the reminders registered in the system. Each reminder has a separate row. All rows in this table consist of the following columns:

1. *PartitionKey* - ServiceId + "_" + GrainRefConsistentHash
2. *RowKey* -  GrainReference + "-" ReminderName
3. *GrainReference* - the grain refernce of the grain that created this reminder.
4. *ReminderName* - the name of this reminder
5. *ServiceId* - the service id of the currently running Orleans service
6. *DeploymentId* - the deployment  id of the currently running Orleans service
7. *StartAt* - the time when this reminder was suppoused to tick in the first time
8. *Period* - the time period for this reminder
9. *GrainRefConsistentHash* - the consistent hash of the GrainReference


## Silo Metrics table

Silo metrics table containes a small set of per-silo important key performance metrics. Each silo has one row, updated periodically by its silo in place.

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
16. **ReceivedMessages* - total number of remote received messages, from other silos as well as from the clients.
17. *LoadShedding* - whether this silo is currently overloaded and is in the load shedding mode.
18. *Clients* - number of currently connected clients


## Clients Metrics table

## Silo Statistics table

## Clients Statistics table

