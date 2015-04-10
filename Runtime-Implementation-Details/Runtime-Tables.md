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
2. *RowKe*y - Silo IP Address + "-" + Silo Port + "-" + Silo Generation number (epoch)
3. *DeploymentId*
4. *Address* - IP address
5. *Port* - silo to silo TCP port
6. *Generatio*n - Generation number (epoch number)
7. *HostName* - silo Hostname
8. *Status* - status of this silo, as set by cluster management protocol. Any of the type [`Orleans.Runtime.SiloStatus`](https://github.com/dotnet/orleans/blob/master/src/Orleans/Runtime/SiloStatus.cs)
9. *ProxyPort* - silo to clients TCP port
10. *Primary* - whether this silo is primary or not. Deprecated.
11. *RoleName** - The name of this role, if running is Azure.
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

## Silo Metrics table

## Clients Metrics table

## Silo Statistics table

## Clients Statistics table

