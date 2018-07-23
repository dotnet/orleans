---
layout: page
title: Multi-Cluster Communication
---

[!include[](../../warning-banner.md)]

## Multi-Cluster Communication

The network must be configured in such a way that any Orleans silo can connect to any other Orleans silo via TCP/IP, regardless of where in the world it is located. Exactly how this is achieved is outside of the scope of Orleans, as it depends on how and where silos are deployed.

For example, on Windows Azure, we can use  VNETs to connect muliple deployments within a region, and gateways to connect VNETs across different regions. 

### Cluster Id

Each cluster has its own unique cluster id. The cluster id must be specified in the global configuration. 

Cluster ids may not be empty, nor may they contain commas. Also, if using Azure Table Storage, cluster ids may not contain the characters forbidden for row keys 
(/, \, #, ?).

We recommend using very short strings for the cluster ids, because cluster ids are transmitted frequently and may be stored in storage by some log-view providers.

### Cluster Gateways

Each cluster automatically designates a subset of its active silos to serve as *cluster gateways*. Cluster gateways directly advertise their IP addresses to other clusters, and can thus serve as  "points of first contact". By default, at most 10 silos (or whatever number is configured as `MaxMultiClusterGateways`) are designated as cluster gateways.

Communication between silos in different clusters does *not* always pass through a gateway. Once a silo has learned and cached the location of a grain activation (no matter in what cluster), it sends messages to that silo directly, even if the silo is not a cluster gateway.

### Gossip

Gossip is a mechanism for clusters to share configuration and status information. As the name suggests, gossip is decentralized and bidirectional: each silo communicates directly with other silos, both in the same cluster and in other clusters, to exchange information in both directions. 

**Content**. Gossip contains some or all of the following information:
- The current time-stamped [multi-cluster configuration](MultiClusterConfiguration.md).
- A dictionary that contains information about cluster gateways. The key is the silo address, and the value contains (1) a timestamp, (2) the cluster id, and (3) a status, which is either active or inactive. 

**Fast & Slow Propagation**. When a gateway changes its status, or when an operator injects a new configuration, this gossip information is immediately sent  to all silos, clusters, and gossip channels. This happens fast, but is not reliable. Should the message be lost due to any reasons (e.g. races, broken sockets, silo failures), our periodic background gossip ensures that the information eventually spreads, albeit more slowly.  All information is eventually propagated everywhere, and is highly resilient to occasional message loss and failures. 

All gossip data is timestamped, which ensures that newer information replaces older information regardless of the relative timing of messages. For example, newer multi-cluster configurations replace older ones, and newer information about a gateway replaces older information about that gateway. For more details on the representation of gossip data, see the `MultiClusterData` class. It has a `Merge` method that combines gossip data, resolving conflicts using timestamps. 

### Gossip Channels

When a silo is first started, or when it is restarted after a failure, it needs to have a way to **bootstrap the gossip**. This is the role of the *gossip channel*, which can be configured in the [Silo Configuration](SiloConfiguration.md). On startup, a silo fetches all the information from the gossip channels. After startup, a silo keeps gossiping periodically, every 30 seconds or whatever is configured as `BackgroundGossipInterval`. Each time it synchronizes its gossip information with a partner randomly selected from all cluster gateways and gossip channels. 

Notes: 
- Though not strictly required, we recommend to always configure at least two gossip channels, in distinct regions, for better availability.  

- Latency of communication with gossip channels is not critical.

- Multiple different services can use the same gossip channel without interference, as long as the ServiceId Guid (as specified by their respective configuration) is distinct.

- There is no strict requirement that all silos use the same gossip channels, as long as the channels are sufficient to let a silo initially connect with the "gossiping community" when it starts up. But if a gossip channel is not part of a  silo's configuration, and that silo is a gateway, it does not push its status updates to the channel (fast propagation), so it may take longer before those reach the channel via periodic background gossip (slow propagation). 

#### Azure-Table-Based Gossip Channel 

We have already implemented a gossip channel based on Azure Tables. The configuration specifies standard connection strings used for Azure accounts. For example, a configuration could specify two gossip channels with separate Azure storage accounts `usa` and `europe` as follows:

```html
<MultiClusterNetwork ClusterId="...">
 <GossipChannel  Type="AzureTable" 
  ConnectionString="DefaultEndpointsProtocol=https;AccountName=usa;AccountKey=..."/>      
 <GossipChannel  Type="AzureTable" 
  ConnectionString="DefaultEndpointsProtocol=https;AccountName=europe;AccountKey=..."/>  
</MultiClusterNetwork>    
```

Multiple different services can use the same gossip channel without interference, as long as the ServiceId guid specified by their respective configuration is distinct.

#### Other Gossip Channel Implementations

We are working on other gossip channel providers, similar to how membership and reminders are packaged for many different storage back-ends.  