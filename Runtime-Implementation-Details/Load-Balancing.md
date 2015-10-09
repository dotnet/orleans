---
layout: page
title: Load balancing in Orleans
---
Load balancing in Orleans applies in several places. Load balancing, in a broad sense, is one of the pillars of the Orleans runtime. Orleans runtime tries to make everything balanced, since balancing allows to maximize resource usage and avoid hotspots, which leads to better performance, as well as helps with elasticity. Below is a non-exhaustive list of places where the runtime performs balancing:

1.	Default actor placement strategy is random - new activations are placed randomly across silos. That results in a more balanced placement and prevents hotspots for most scenarios. 

2.	A more advanced ActivationCountPlacement tries to equalize the number of activations on all silos, which results in a more even distribution. This is especially important for elasticity.

3.	Grain Directory service is built on top of a Distributed Hash Table, which inherently is balanced. The directory service maps grains to activations, each silo owns part of the global mapping table, this table is globally partitioned in a balanced way across all silos. We use consistent hashing with virtual buckets for that.

4.	Clients connect to all gateways and spread their requests across them, in a balanced way.

5.	Reminder service is a partitioned runtime service. The responsibility of which silos is responsible to server which reminder is balanced across all silo via consistent hashing, just like in the grain directory case.

6.	Performance critical components within a silo are partitioned, and the work across them is locally balanced. That way the silo runtime can fully utilize all available CPU cores and not create in-silo bottlenecks. This applies to all local resources: allocation of work to threads, sockets, dispatch responsibilities, queues, etc.

7.	StreamQueueBalance balances the responsibility of pulling events from persistence queues across silos in the cluster.

Also notice that balancing, in a broad sense, does not necessarily mean loss of locality. One can be balanced and still maintain a good locality. For example, when balancing means sharding/partitioning, you can partition responsibility for a certain logical task but within each partition have locality. That applies both for local and distributed balancing.
