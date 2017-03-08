---
layout: page
title: Using Consul as a Membership Provider
---

# Using Consul as a Membership Provider

## Introduction to Consul
[Consul](https://www.consul.io) is a distributed, highly available and datacenter-aware service discovery platform which includes simple service registration, health checking, failure detection and key/value storage.  It is built on the premise that every node in the datacenter is running a Consul agent which is either acting as a server or client which communicate via a scalable gossip protocol.

There is a very detailed overview of Consul including comparisons with similar solutions [here](https://www.consul.io/intro/index.html).

Consul is written in GO and is [open source](https://github.com/hashicorp/consul); compiled downloads are available for [Mac OS X, FreeBSD, Linux, Solaris and Windows](https://www.consul.io/downloads.html)

## Why Choose Consul?
As an [Orleans Membership Provider](Cluster-Management.md), Consul is a good choice when you need to deliver an **on-premise solution** which does not require your potential customers to have existing infrastructure **and** a co-operative IT provider.  Consul is a very lightweight single executable, has no dependencies and as such can easily be built into your own middleware solution.  And when Consul is already your solution for discovering, checking and maintaining your microservices, it makes sense to fully integrate with Orleans membership for simplicity and ease of operation. We therefore implemented a membership table in Consul (also known as "Orleans Custom System Store"), which fully integrates with Orleans's [Cluster Management](Cluster-Management.md).

## Setting up Consul
There is very extensive documentation available on [Consul.io](https://www.consul.io) about setting up a stable Consul cluster and it doesn't make sense to repeat that here; however for your convenience we include this guide so you can very quickly get Orleans running with a standalone Consul agent.

1) Create a folder to install Consul into, e.g. C:\Consul

2) Create a subfolder: C:\Consul\Data (Consul will not create this if it doesn't exist)

3) [Download](https://www.consul.io/downloads.html) and unzip Consul.exe into C:\Consul\

4) Open a command prompt at C:\Consul\

5) Enter `Consul.exe agent -server -bootstrap -data-dir "C:\Consul\Data" -client=0.0.0.0`

`agent` Instructs Consul to run the agent process that hosts the services.  Without this the Consul process will attempt to use RPC to configure a running agent.

`-server` Defines the agent as a server and not a client (A Consul *client* is an agent that hosts all the services and data, but does not have voting rights to decide, and cannot become, the cluster leader

`-bootstrap` The first (and only the first!) node in a cluster must be bootstrapped so that it assumes the cluster leadership.

`-data-dir [path]` Specifies the path where all Consul data is stored, including the cluster membership table

`-client=0.0.0.0` Informs Consul which IP to open the service on.

There are many other parameters, and the option to use a json configuration file.  Please consult the Consul documentation for a full listing of the options.

6) Verify that Consul is running and ready to accept membership requests from Orleans by opening the [services](http://localhost:8500/v1/catalog/services) endpoint in your browser.

## Configuration of Orleans

### Server
There is currently a known issue with the "Custom" membership provider OrleansConfiguration.xml configuration file that will fail to parse correctly.  For this reason you have to provide a placeholder SystemStore in the xml and then configure the provider in code before starting the Silo.

**OrleansConfiguration.xml**

```xml
<OrleansConfiguration xmlns="urn:orleans">
    <Globals>
        <SystemStore SystemStoreType="None" DataConnectionString="http://localhost:8500" DeploymentId="MyOrleansDeployment" />
    </Globals>
    <Defaults>
        <Networking Address="localhost" Port="22222" />
        <ProxyingGateway Address="localhost" Port="30000" />
    </Defaults>    
</OrleansConfiguration>
```

**Code**

```csharp
public void Start(ClusterConfiguration config)
{
    _siloHost = new SiloHost(System.Net.Dns.GetHostName(), config);

    _siloHost.Config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.Custom;
    _siloHost.Config.Globals.MembershipTableAssembly = "OrleansConsulUtils";
    _siloHost.Config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.Disabled;

    _siloHost.InitializeOrleansSilo();
    var startedok = _siloHost.StartOrleansSilo();
    if (!startedok)
        throw new SystemException(String.Format("Failed to start Orleans silo '{0}' as a {1} node", _siloHost.Name, _siloHost.Type));

    Log.Information("Orleans Silo is running.\n");
}
```

Alternatively you could configure the silo entirely in code.

### Client

The client configuration is much simpler

**ClientConfiguration.xml**

```xml
<ClientConfiguration xmlns="urn:orleans">
    <SystemStore SystemStoreType="Custom" CustomGatewayProviderAssemblyName="OrleansConsulUtils" DataConnectionString="http://192.168.1.26:8500" DeploymentId="MyOrleansDeployment" />
</ClientConfiguration>
```

## Client SDK
If you are interested in using Consul for your own service discovery there are [Client SDKs](https://www.consul.io/downloads_tools.html) for most popular languages.

## Implementation Detail

The Membership Table Provider makes use of [Consul's Key/Value store](https://www.consul.io/intro/getting-started/kv.html) functionality with CAS.  When each Silo starts it registers two KV entries, one which contains the Silo details and one which holds the last time the Silo reported it was alive (the latter refers to diagnostics "I am alive" entries and not to failure detection hearbeats which are sent directly between the silos and are not written into the table). All writes to the table are performed with CAS to provide concurrency control, as necessitated by Orleans's [Cluster Management Protocol](Cluster-Management.md). Once the Silo is running you can view these entries in your web browser [here](http://localhost:8500/v1/kv/?keys), this will display something like:

```js
[
    "orleans/MyOrleansDeployment/192.168.1.26:11111@191780753",
    "orleans/MyOrleansDeployment/192.168.1.26:11111@191780753/iamalive"
]
```

You will notice that the keys are prefixed with *"orleans/"* this is hard coded in the provider and is intended to avoid key space collision with other users of Consul.  Each of these keys can be read by appending their key name *(sans quotes of course)* to the [Consul KV root](http://localhost:8500/v1/kv/).  Doing so will present you with the following:

```js
[
    {
        "LockIndex": 0,
        "Key": "orleans/MyOrleansDeployment/192.168.1.26:22222@191780753",
        "Flags": 0,
        "Value": "[BASE64 UTF8 Encoded String]",
        "CreateIndex": 10,
        "ModifyIndex": 12
    }
]
```

Decoding the string will give you the actual Orleans Membership data:

**http://localhost:8500/v1/KV/orleans/MyOrleansDeployment/[SiloAddress]**

```
{
    "Hostname": "[YOUR_MACHINE_NAME]",
    "ProxyPort": 22222,
    "StartTime": "2016-01-29T16:25:54.9538838Z",
    "Status": 3,
    "SuspectingSilos": []
}
```

**http://localhost:8500/v1/KV/orleans/MyOrleansDeployment/[SiloAddress]/IAmAlive**

	"2016-01-29T16:35:58.9193803Z"

When the Clients connect, they read the KVs for all silos in the cluster in one HTTP GET by using the uri `http://192.168.1.26:8500/v1/KV/orleans/MyOrleansDeployment/?recurse`.

## Limitations

### Orleans Extended Membership Protocol (Table Version & ETag)
Consul KV currrently does not currently support atomic updates. Therefore, the Orleans Consul Membership Provider only implements the the Orleans Basic Membership Protocol, as described [here](Cluster-Management.md) and does not support the Extended Membership Protocol.  This Extended protocol was introduced as an additional, but not essential, silo connectivity validation and as a foundation to functionality that has not yet been implemented. Providing your infrastructure is correctly configured you will not experience any detrimental effect of the lack of support.

### Multiple Datacenters
The Key Value Pairs in Consul are not currently replicated between Consul datacenters.  There is a [separate project](https://github.com/hashicorp/consul-replicate) to address this but it has not yet been proven to support Orleans.

### When running on Windows

When Consul starts on Windows it logs the following message:

	==> WARNING: Windows is not recommended as a Consul server. Do not use in production.

This is displayed simply due to lack of focus on testing when running in a Windows environment and not because of any actual known issues.  Read the [discussion here](https://groups.google.com/forum/#!topic/consul-tool/DvXYgZtUZyU) before deciding if Consul is the right choice for you.

## Potential Future Enhanecements

1) Prove that the Consul KV replication project is able to support an Orleans cluster in a WAN environment between multiple Consul datacenters.

2) Implement the Reminder Table in Consul.

3) Implement the Extended Membership Protocol. The team behind Consul does plan on implementing atomic operations, once this functionality is available it will be possible to remove the limitations in the provider.
