---
layout: page
title: Server Configuration
---

[!include[](../../../warning-banner.md)]

# Server Configuration

There are two key aspects of silo configuration:

* Connectivity: silo’s endpoints for other silos and clients
* Cluster membership and reliability: how silos discover each other in a deployment and detect node failures.

Depending on the environment you want to run Orleans in some of these parameters may or may not be important.
For example, for a single silo development environment one usually doesn’t need reliability, and all the endpoints can be localhost.

The following sections detail the configuration setting for the four mentioned key aspects.
Then in the scenarios section you can find the recommended combinations of the settings for the most typical deployment scenarios.

## Connectivity
The connectivity settings define two TCP/IP endpoints: one for inter-silo communication and one for client connections, also referred to as client gateway or simply gateway.

**Inter-silo Endpoint**

``` xml
<Networking Address=" " Port="11111" />
```

 Address: IP address or host name to use. If left empty, silo will pick the first available IPv4 address. Orleans supports IPv6 as well as IPv4.

 Port: TCP port to use. If left empty, silo will pick a random available port. If there is only one silo running on the machine, it is advisable to specify a port for consistency and for easy configuration of the firewall. For running multiple silos on the same machine, you can either provide each of the silos with different configuration files or leave the Port attribute empty for a random port assignment.

 For machines that have more than one IP address assigned to them, if you need to choose an address from a specific subnet or an IPv6 address, you can do that by adding a Subnet and PreferredFamily attributes respectively (refer to the XSD schema for exact syntax of those attributes).

 For local development environment, you can simply use localhost as the host name:


``` xml
<Networking Address="localhost" Port="11111" />
```

## Client Gateway Endpoint

 The setting for client gateway endpoint is identical to the inter-silo endpoint except for the XML element name:


``` xml
<ProxyingGateway Address="localhost" Port="30000" />
```

 You have to specify a port number different from the one used for the inter-silo endpoint.

 It is possible to configure clients to connect to the inter-silo endpoint instead of the gateway, but that requires opening a listening socket on the client (thus requires enabling incoming connections on the client machine firewall), and in general is not advisable other than for a very limited set of scenarios.

## Cluster Membership and Reliability

 Usually, a service built on Orleans is deployed on a cluster of nodes, either on dedicated hardware or in Azure. For development and basic testing, Orleans can be deployed in a single node configuration. When deployed to a cluster of nodes, Orleans internally implements a set of protocols to discover and maintain membership of Orleans silos in the cluster, including detection of node failures and automatic reconfiguration.

 For reliable management of cluster membership, Orleans uses Azure Table, SQL Server or Apache ZooKeeper for synchronization of nodes. The reliable membership setup requires configuring the 'SystemStore' element settings in the silo configuration file:


``` xml
<SystemStore SystemStoreType="AzureTable"
             DeploymentId="..."
             DataConnectionString="..."/>
```

 or

``` xml
<SystemStore SystemStoreType="SqlServer"
             DeploymentId="..."
             DataConnectionString="..."/>

```

 or

``` xml
<SystemStore SystemStoreType="ZooKeeper"
             DeploymentId="..."
             DataConnectionString="..."/>

```

 DeploymentId is a unique string that defines a particular deployment. When deploying an Orleans based service to Azure it makes most sense to use the Azure deployment ID of the worker role.

 For development or if it’s not possible to use Azure Table, silos can be configured to use the membership grain instead. Such a configuration is unreliable as it will not survive a failure of the primary silo that hosts the membership grain. “MembershipTableGrain” is the default value of LivenessType.


``` xml
<Liveness LivenessType ="MembershipTableGrain" />
```

## Primary Silo
In a reliable deployment, one that is configured with membership using Azure Table, SQL Server or ZooKeeper, all silos are created equal, with no notion of primary or secondary silos. That is the configuration that is recommended for production that will survive a failure of any individual node or a combination of nodes. For example, Azure periodically rolls out OS patches and that causes all of the role instances to reboot eventually.

 For development or a non-reliable deployment when MembershipTableGrain is used, one of the silos has to be designated as Primary and has to start and initialize before other, Secondary, silos that wait for Primary to initialize before joining the cluster. In case of a failure of the Primary node, the whole deployment stops working properly and has to be restarted.

Primary is designated in the configuration file with the following setting within the Globals section.


``` xml
<SeedNode Address="<host name or IP address of the primary node>" Port="11111" />
```

Here is an example how to configure and launch Orleans silo hosted inside worker-role.
This is a reference only example and SHOULD NOT be used AS-IS - you may need to fine-tune client parameters for your specific environment.

```csharp
var dataConnection = "DefaultEndpointsProtocol=https;AccountName=MYACCOUNTNAME;AccountKey=MYACCOUNTKEY";

var config = new ClusterConfiguration
{
    Globals =
    {
        DeploymentId = RoleEnvironment.DeploymentId,
        ResponseTimeout = TimeSpan.FromSeconds(30),
        DataConnectionString = dataConnection,

        LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable,
        ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.AzureTable,
    },
    Defaults =
    {
        PropagateActivityId = true,

        // Tracing
        DefaultTraceLevel = Severity.Info,
        TraceToConsole = false,
        TraceFilePattern = @"Silo_{0}-{1}.log",
        //TraceFilePattern = "false", // Set it to false or none to disable file tracing, effectively it sets config.Defaults.TraceFileName = null;
        TraceLevelOverrides =
        {
            Tuple.Create("ComponentName", Severity.Warning),
        }
    }
};

// Register bootstrap provider class
config.Globals.RegisterBootstrapProvider<AutoStartBootstrapProvider>("MyAutoStartBootstrapProvider");

// Add Storage Providers
config.Globals.RegisterStorageProvider<MemoryStorage>("MemoryStore");

config.Globals.RegisterStorageProvider<AzureTableStorage>("PubSubStore",
    new Dictionary<string, string>
    {
        { "DeleteStateOnClear", "true" },
        //{ "UseJsonFormat", "true" },
        { "DataConnectionString", dataConnection }
    });

config.Globals.RegisterStorageProvider<AzureTableStorage>("AzureTable",
    new Dictionary<string, string>
    {
        { "DeleteStateOnClear", "true" },
        { "DataConnectionString", dataConnection }
    });

config.Globals.RegisterStorageProvider<AzureTableStorage>("DataStorage",
    new Dictionary<string, string>
    {
        { "DeleteStateOnClear", "true" },
        { "DataConnectionString", dataConnection }
    });

config.Globals.RegisterStorageProvider<BlobStorageProvider>("BlobStorage",
    new Dictionary<string, string>
    {
        { "DeleteStateOnClear", "true" },
        { "ContainerName", "grainstate" },
        { "DataConnectionString", dataConnection }
    });

// Add Stream Providers
config.Globals.RegisterStreamProvider<AzureQueueStreamProvider>("AzureQueueStreams",
    new Dictionary<string, string>
    {
        { "PubSubType", "ExplicitGrainBasedAndImplicit" },
        { "DeploymentId", "orleans-streams" },
        { "NumQueues", "4" },
        { "GetQueueMessagesTimerPeriod", "100ms" },
        { "DataConnectionString", dataConnection }
    });

try
{
    _orleansAzureSilo = new AzureSilo();
    var ok = _orleansAzureSilo.Start(config, config.Globals.DeploymentId, config.Globals.DataConnectionString);

    _orleansAzureSilo.Run(); // Call will block until silo is shutdown
}
catch (Exception exc)
{
    //Log "Error when starting Silo"
}

```
