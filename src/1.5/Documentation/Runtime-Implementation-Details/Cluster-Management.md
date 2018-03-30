---
layout: page
title: Cluster Management in Orleans
---

[!include[](../../warning-banner.md)]

# Cluster Management in Orleans

Orleans provides cluster management via a built-in membership protocol, which we sometimes refer to as **Silo Membership**. The goal of this protocol is for all silos (Orleans servers) to agree on the set of currently alive silos, detect failed silos, and allow new silos to join the cluster.

The protocol relies on an external service to provide an abstraction of `MembershipTable`. `MembershipTable` is a flat No-SQL like durable table that we use for 2 purposes. First, it is used as a rendezvous point for silos to find each other and Orleans clients to find silos. Second, it is used to store the current membership view  (list of alive silos) and helps coordinate the agreement on the membership view. We currently have 6 implementations of the `MembershipTable`: based on [Azure Table Storage](https://docs.microsoft.com/en-us/azure/storage/storage-dotnet-how-to-use-tables), SQL server, [Apache ZooKeeper](https://ZooKeeper.apache.org/), [Consul IO](https://www.consul.io), [AWS DynamoDB](https://aws.amazon.com/dynamodb/), and in-memory emulation for development.
In addition to the `MembershipTable` each silo participates in fully distributed peer-to-peer membership protocol that detects failed silos and reaches agreement on a set alive silos. We start by describing the internal implementation of the Orleans's membership protocol below and later on describe the implementation of the `MembershipTable`.


### The Basic Membership Protocol:

1. Upon startup every silo writes itself into a well-known `MembershipTable` (passed via config). A combination of silo identity (`ip:port:epoch`) and service deployment id are used as unique keys in the table. Epoch is just time in ticks when this silo started, and as such `ip:port:epoch` is guaranteed to be unique in a given Orleans deployment.

2. Silos monitor each other directly, via application pings ("are you alive" `heartbeats`). Pings are sent as direct messages from silo to silo, over the same TCP sockets that silos communicate. That way, pings fully correlate with actual networking problems and server health. Every silo pings X other silos. A silo picks whom to ping by calculating consistent hashes on other silos' identity, forming a virtual ring of all identities and picking X successor silos on the ring (this is a well-known distributed technique called [consistent hashing](https://en.wikipedia.org/wiki/Consistent_hashing) and is widely used in many distributed hash tables, like [Chord DHT](https://en.wikipedia.org/wiki/Chord_(peer-to-peer))).

3. If a silo S does not get Y ping replies from a monitored servers P, it suspects it by writing its timestamped suspicion into P’s row in the `MembershipTable`.

4. If P has more than Z suspicions within K seconds, then S writes that P is dead into P’s row, and broadcasts a request for all silos to re-read the membership table (which they’ll do anyway periodically).

5. In more details:

	5.1 Suspicion is written to the `MembershipTable`, in a special column in the row corresponding to P. When S suspects P it writes: “at time TTT S suspected P”.

	5.2 One suspicion is not enough to declare P as dead. You need Z suspicions from different silos in a configurable time window T, typically 3 minutes, to declare P as dead. The suspicion is written using optimistic concurrency control provided by the `MembershipTable`.

	5.3 The suspecting silo S reads P's row.
	
        5.4 If S is the last suspector (there have already been Z-1 suspectors within time period T, as written in the suspicion column), S decides to declare P as Dead. In this case, S adds itself to list of suspectors and also writes in P's Status column that P is Dead.
        
        5.5 Otherwise, if S is not the last suspector, S just adds itself to the suspectors column.

	5.6 In either case the write back uses the version number or etag that was read, so the updates to this row are serialized. In case the write has failed due to version/etag mismatch, S retries (read again, and try to write, unless P was already marked dead).

	5.7 At a high level this sequence of "read, local modify, write back" is a transaction. However, we are not using storage transactions to do that. “Transaction” code executes locally on a server and we use optimistic concurrency provided by the `MembershipTable` to ensure isolation and atomicity.

6. Every silo periodically reads the entire membership table for its deployment. That way silos learn about new silos joining and about other silos being declared dead.

7. **Configuration**: we provide a default configuration, which was hand tuned during our production usage in Azure. Currently the default is: every silo is monitored by 3 other silos, 2 suspicions are enough to declare a silo dead, suspicions only from last 3 minutes (otherwise they are outdated). Pings are send every 10 seconds and you needs to miss 3 pings to suspect a silo.

8. **Enforcing Perfect Failure detection** – it is theoretically possible that a silo will be declared dead if it lost communication with other silos, while the silo process itself is still running. To solve this problem once the silo is declared dead in the table it is considered dead by everyone, even if it is in fact not dead (just partitioned temporarily or heartbeat messages got lost). Everyone stops communicating with it and once it learns that it is dead (by reading its own new status from the table) it commits suicide and shuts down its process. As a result, there must be an infrastructure in place to restart the silo as a new process (a new epoch number is generated upon start). When it's hosted in Azure, that happens automatically. When it isn't, another infrastructure is required. For example, a Windows Service configured to auto restart on failure.

9. **Optimization  to reduce the frequency of periodical table reads and speed up all silos learning about new joining silos and dead silos**. Every time any silo writes anything successfully to the table (suspicion, new join, …) it also broadcasts to all other silos – “go and reread the table now”. The silo does NOT tell others what it wrote in the table (since this information could already be outdated/wrong), it just tells them to re-read the table. That way we learn very quickly about membership changes without the need to wait for the full periodic read cycle. We still need the periodic read, in case the “re-read the table” message gets lost.

### Properties of the Basic Membership Protocol and FAQ:

1. **Can handle any number of failures** – our algorithm can handle any number of failures (that is, f<=n), including full cluster restart. This is in contrast with “traditional” [Paxos](https://en.wikipedia.org/wiki/Paxos_(computer_science)) based solutions, which require quorum, which is usually a majority. We have seen in production situations when more than half of the silos were down. Our system stayed functional, while Paxos based membership would not be able to make progress.
2. **Traffic to the table is very light** - The actual pings go directly between servers and not to the table. This would generate a lot of traffic plus would be less accurate from the failure detection perspective - if a silo could not reach the table, it would miss to write its I am alive heartbeat and others would kill him.
3. **Tunable accuracy vs. completeness** – [both perfect and accurate failure detection is not possible in general](http://www.cs.yale.edu/homes/aspnes/pinewiki/FailureDetectors.html). One usually wants an ability to tradeoff accuracy (don’t want to declare a silo that is really alive as dead) with completeness (want to declare dead a silo that is indeed dead as soon as possible). The configurable #votes to declare dead and  #missed pings allows to trade those two.

4. **Scale** - the basic protocol can handle thousands and probably even tens of thousands of servers. This is in contrast with traditional [Paxos](https://en.wikipedia.org/wiki/Paxos_(computer_science)) based solutions, such as group communication protocols, which are known not to scale beyond tens.

5. **Diagnostics** - the table is also very convenient for diagnostics and troubleshooting. System administrator can instantaneously find in the table the current list of alive silos, as well as see the history of all killed silos and suspicions. This is especially useful when diagnosing problems.

6. **Why do we need reliable persistent storage for implementation of the `MembershipTable`?** - we use persistent storage (Azure table, SQL server, AWS DynamoDB, Apache ZooKeeper or Consul IO KV) for the `MembershipTable` for 2 purposes. First, it is used as a rendezvous point for silos to find each other and Orleans clients to find silos. Second, we use the reliable storage to help us coordinate the agreement on the membership view. While we perform failure detection directly in a peer to peer fashion between the silos, we store the membership view in a reliable storage and use the concurrency control mechanism provided by this storage to reach agreement of who is alive and who is dead. That way, in a sense, our protocol outsources the hard problem of distributed consensus to the cloud. In that we fully utilize the power of the underlying cloud platform, using it truly as "Platform as a Service".

7. **What happens if the table is not accessible for some time?** (storage service is down, unavailable, or there are communication problems with it) – our protocol will NOT declare silos as dead by mistake in such a case.  Currently operational silos will keep working without any problems. However, we won't be able to declare a silo dead (if we detected some silo is dead via missed pings we won’t be able to write this fact to the table) and also won't be able to allow new silos to join. So completeness will suffer, but accuracy will not - partitioning from the table will never cause us to declare silo as dead by mistake. Also, in case of a partial network partition (if some silos can access the table and some not), it could happen that we will declare a dead silo as dead, but it will take some time until all other silos learn about it. So detection could be delayed, but we will never wrongly kill someone due to table un-availability.

8. **Direct IAmAlive writes into the table for diagnostics only** - in addition to heartbeats that are sent between the silos, each silo also periodically updates an "I Am Alive" column in his row in the table. This "I Am Alive" column is only used **for manual troubleshooting and diagnostics** and is not used by the membership protocol itself. It is usually written at much lower frequency (once every 5 minutes) and serves as a very useful tool for system administrators to check the liveness of the cluster or easily find out when the silo was last alive.


### Extension to totally order membership views:

The basic membership protocol described above was later extended to support totally ordered membership views. We will briefly describe the reasons for this extension and how it is implemented. The extension does not change anything in the above design, just adds an additional property that all membership configurations are globally totally ordered.

**Why it is useful to totally order membership views?**

* This allows serializing the joining of new silos to the cluster. That way, when a new silo joins the cluster it can validate two-way connectivity to every other silo that has already started. If some of the already joined silos do not answer it (potentially indicating a network connectivity problem with the new silo), the new silo is not allowed to join. This ensures that at least when a silo starts, there is a full connectivity between all silos in the cluster (this is implemented).

* Higher level protocols in the silo, such as distributed grain directory, can utilize the fact that membership views are ordered and use this information to perform smarter duplicate activations resolution. In particular, when directory finds out that 2 activations were created when membership was in flux, it may decide to deactivate the older activation that was created based on the now-outdated membership information (this is currently not implemented).

**Extended Membership Protocol:**

1. For implementation of this feature we utilize the support for transactions over multiple rows that is provided by the `MembershipTable`..

2. We add a membership-version row to the table that tracks table changes.

3. When silo S wants to write suspicion or death declaration for silo P:

    3.1 S reads the latest table content. If P is already dead, do nothing. Otherwise,

    3.2 In the same transaction, write the changes to P's row as well as increment the version number and write it back to the table.

    3.3 Both writes are conditioned with eTags.

    3.4 If transaction aborts due to eTag mismatch on either P's row or on the version row, attempt again.

4. All writes to the table modify and increment the version row. That way all writes to the table are serialized (via serializing the updates to the version row) and since silos only increment the version number, the writes are also totally ordered in increasing order.

**Scalability of the Extended Membership Protocol:**

In the extended version of the protocol all writes are serialized via one row. This can potentially hurt the scalability of the cluster managemenet protocol, since it increases the risk of conflicts between concurrent table writes. To partially mitigate this problem silos retry all their writes to the table by using exponential backoff. We have observed the extended protocols to work smoothly in production environment in Azure with up to 200 silos. However, we do think the protocol might have problems to scale beyond a thousand silos. In such large setups the updates to version row may be easily disabled, essentially maintaining the rest of the cluster managemenet protocol and giving up on the total ordering property. Please also note that we refer here to the scalability of the cluster management protocol, not the rest of Orleans. We believe that other parts of the Orleans runtime (messaging, distributed directory, grain hosting, client to gateway connectivity) are scalable way beyond hundreds of silos.

### Membership Table:

As already mentioned, `MembershipTable` is used as a rendezvous point for silos to find each other and Orleans clients to find silos and also helps coordinate the agreement on the membership view. We currently have 6 implementation of the `MembershipTable`: based on Azure Table, SQL server, Apache ZooKeeper, Consul IO, AWS DynamoDB, and in-memory emulation for development. The interface for `MembershipTable` is defined in [**`IMembershipTable`**](https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs).

1.  [Azure Table Storage](https://docs.microsoft.com/en-us/azure/storage/storage-dotnet-how-to-use-tables) - in this implementation we use Azure deployment ID as partition key and the silo identity (`ip:port:epoch`) as row key. Together they guarantee a unique key per silo. For concurrency control we use optimistic concurrency control based on [Azure Table ETags](https://docs.microsoft.com/en-us/rest/api/storageservices/Update-Entity2). Every time we read from the table we store the etag for every read row and use that eTag when we try to write back. etags are automatically assigned and checked by Azure Table service on every write. For multi-row transactions we utilize the support for [batch transactions provided by Azure table](https://docs.microsoft.com/en-us/rest/api/storageservices/Performing-Entity-Group-Transactions), which guarantees serializale transactions over rows with the same partition key.

2.  SQL Server - in this implementation the configured deployment ID is used to distinguish between deployments and which silos belong to which deployments. The silo identity is defined as a combination of `deploymentID, ip, port, epoch` in appropriate tables and columns. The relational backend uses optimistic concurrency control and transactions, similar to the procedure of using ETags on Azure Table implementation. The relational implementation expects the database engine to generate the ETag used. In case of SQL Server, on SQL Server 2000 the generated ETag is one acquired from a call to [NEWID()](https://www.microsoft.com/en-us/download/details.aspx?id=51958). On SQL Server 2005 and later [ROWVERSION]( https://docs.microsoft.com/en-us/sql/t-sql/data-types/rowversion-transact-sql) is used. Orleans reads and writes relational ETags as opaque `VARBINARY(16)` tags and stores them in memory as [base64]( https://en.wikipedia.org/wiki/Base64) encoded strings. Orleans supports multi-row inserts using UNION ALL (for Oracle including DUAL), which is currently used to insert statistics data. The exact implementation and rationale for SQL Server can be seen at [CreateOrleansTables_SqlServer.sql](https://github.com/dotnet/orleans/blob/ba30bbb2155168fc4b9f190727220583b9a7ae4c/src/OrleansSQLUtils/CreateOrleansTables_SqlServer.sql).

3.  [Apache ZooKeeper](https://ZooKeeper.apache.org/) - in this implementation we use the configured deployment ID as a root node and the silo identity (`ip:port@epoch`) as its child node. Together they guarantee a unique path per silo. For concurrency control we use optimistic concurrency control based on the [node version](http://zookeeper.apache.org/doc/r3.4.6/zookeeperOver.html#Nodes+and+ephemeral+nodes). Every time we read from the deployment root node we store the version for every read child silo node and use that version when we try to write back. Each time a node's data changes, the version number increases atomically by the ZooKeeper service. For multi-row transactions we utilize the [multi method](http://zookeeper.apache.org/doc/r3.4.6/api/org/apache/zookeeper/ZooKeeper.html#multi(java.lang.Iterable)), which guarantees serializale transactions over silo nodes with the same parent deployment ID node.

4.  [Consul IO](https://www.consul.io) - we used [Consul's Key/Value store](https://www.consul.io/intro/getting-started/kv.html) to impelement the membershop table. Refer to [Consul-Deployment](Consul-Deployment.md) for more details.

5.  [AWS DynamoDB](https://aws.amazon.com/dynamodb/) - In this implementation we use the cluster Deployment ID as the Partition Key and  Silo Identity (`ip-port-generation`) as the RangeKey making the record unity. The optimistic concurrency is made by the `ETag` attribute by making conditional writes on DynamoDB. The implementation logic is quite similar to Azure Table Storage. We only implemented the basic membership protocol (and not the extended protocol). 

6. In-memory emulation for development setup. We use a special system grain, called `MembershipTableGrain`, for that implementation. This grain lives on a designated primary silo, which is only used for a **development setup**. In any real production usage primary silo **is not required**.


### Configuration:

Membership protocol is configured via the `Liveness` element in the `Globals` section in `OrleansConfiguration.xml` file.
The default values were tuned in years of production usage in Azure and we believe they represent good default settings. There is no need in general to change them.

Sample config element:

```xml
<Liveness ProbeTimeout = "5s" TableRefreshTimeout ="10s  DeathVoteExpirationTimeout ="80s" NumMissedProbesLimit = "3" NumProbedSilos="3" NumVotesForDeathDeclaration="2" />
```

There are 4 types of liveness implemented. The type of the liveness protocol is configured via the `SystemStoreType` attribute of the `SystemStore` element in the `Globals` section in `OrleansConfiguration.xml` file.

1. `MembershipTableGrain` - membership table is stored in a grain on primary silo. This is a **development setup only**.

2. `AzureTable` - membership table is stored in Azure table.

3. `SqlServer` - membership table is stored in a relational database.

4. `ZooKeeper` - membership table is stored in a ZooKeeper [ensemble](http://zookeeper.apache.org/doc/r3.4.6/zookeeperAdmin.html#sc_zkMulitServerSetup).

5. `Consul` - configured as Custom system store with `MembershipTableAssembly = "OrleansConsulUtils"`.  Refer to [Consul-Deployment](Consul-Deployment.md) for more details.

6. `DynamoDB` - configured as a Custom system store with `MembershipTableAssembly = "OrleansAWSUtils"`.

For all liveness types the common configuration variables are defined in `Globals.Liveness` element:

1. `ProbeTimeout` - The number of seconds to probe other silos for their liveness or for the silo to send "I am alive" heartbeat messages about itself. Default is 10 seconds.

2. `TableRefreshTimeout` - The number of seconds to fetch updates from the membership table. Default is 60 seconds.

3. `DeathVoteExpirationTimeout` - Expiration time in seconds for death vote in the membership table. Default is 120 seconds

4. `NumMissedProbesLimi`t - The number of missed "I am alive" heartbeat messages from a silo or number of un-replied probes that lead to suspecting this silo as dead. Default is 3.

5. `NumProbedSilos` - The number of silos each silo probes for liveness. Default is 3.

6. `NumVotesForDeathDeclaration` - The number of non-expired votes that are needed to declare some silo as dead (should be at most NumMissedProbesLimit). Default is 2.

7. `UseLivenessGossip` - Whether to use the gossip optimization to speed up spreading liveness information. Default is true.

8. `IAmAliveTablePublishTimeout` - The number of seconds to periodically write in the membership table that this silo is alive. Used only for diagnostics.  Default is 5 minutes.

9. `NumMissedTableIAmAliveLimit` - The number of missed "I am alive" updates  in the table from a silo that causes warning to be logged. Does not impact the liveness protocol. Default is 2.

10. `MaxJoinAttemptTime` - The number of seconds to attempt to join a cluster of silos before giving up. Default is 5 minutes.

11. `ExpectedClusterSize` - The expected size of a cluster. Need not be very accurate, can be an overestimate. Used to tune the exponential backoff algorithm of retries to write to Azure table. Default is 20.

### Design Rationale:

A natural question that might be asked is why not to rely completely on [Apache ZooKeeper](https://ZooKeeper.apache.org/) for the cluster membership implementation, potentially by using it's out of the box support for [group membership with ephemeral nodes] (http://zookeeper.apache.org/doc/trunk/recipes.html#sc_outOfTheBox)? Why did we bother implementing our own membership protocol? There were primarily three reasons:

1) **Deployment/Hosting in the Cloud** - Zookeeper is not a hosted service (at least at the time of this writing July 2015 and definitely when we first implemented this protocol in the summer of 2011 there was no version of Zookeeper running as a hosted service by any major cloud provider). It means that in the Cloud environment Orleans customers would have to deploy/run/manage their own instance of a ZK cluster. This is just yet another unnecessary burden, that we did not want to force on our customers. By using Azure Table we rely on a hosted, managed service which makes our customers lives much simpler. _Basically, in the Cloud, use Cloud as a Platform, not as an Infrastructure._ On the other hand, when running on premises and managing your own servers, relying on ZK as an implementation of the `MembershipTable` is a viable option.

2) **Direct failure detection** - when using ZK's group membership with ephemeral nodes the failure detection is performed between the Orleans servers (ZK clients) and ZK servers. This may not necessarily correlate with the actual network problems between Orleans servers. _Our desire was that the failure detection would accurately reflect the intra-cluster state of the communication._ Specifically, in our design, if an Orleans silo cannot communicate with the `MembershipTable` it is not considered dead and can keep working. As opposite to that, have we used ZK group membership with ephemeral nodes a disconnection from a ZK server may cause an Orleans silo (ZK client) to be declared dead, while it may actually be alive and fully functional.

3) **Portability and flexibility** - as part of Orleans's philosophy, we do not want to force a strong dependence on any particular technology, but rather have a flexible design where different components can be easily switched with different implementations. This is exactly the purpuse that `MembershipTable` abstraction serves.


### Acknowledgements:

We would to acknowledge the contribution of [Alex Kogan](https://www.linkedin.com/in/alex-kogan-3a2b52) to the design and implementation of the first version of this protocol. This work was done as part of summer internship in Microsoft Research in the Summer of 2011.
The implementation of ZooKeeper based `MembershipTable` was done by [Shay Hazor](https://github.com/shayhatsor), the implementation of SQL `MembershipTable` was done by [Veikko Eeva](https://github.com/veikkoeeva), the implementation of AWS DynamoDB `MembershipTable` was done by [Gutemberg Ribeiro](https://github.com/galvesribeiro/) and the implementation of Consul based `MembershipTable` was done by [Paul North](https://github.com/PaulNorth).
