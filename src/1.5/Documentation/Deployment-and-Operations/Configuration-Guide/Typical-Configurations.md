---
layout: page
title: Typical Configurations
---

[!include[](../../../warning-banner.md)]

# Typical Configurations

Below are examples of typical configurations that can be used for development and production deployments.

## Local Development
For local development, where there is only one silo running locally on the programmer’s machine, the configuration is already included in the *Orleans Dev/Test Host* project template of [Microsoft Orleans Tools for Visual Studio](https://marketplace.visualstudio.com/items?itemName=sbykov.MicrosoftOrleansToolsforVisualStudio). The local silo that can be started by running a project created with the *Orleans Dev/Test Host* template is configured as follows in DevTestServerConfiguration.xml.

``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <SeedNode Address="localhost" Port="11111" />
  </Globals>
  <Defaults>
    <Networking Address="localhost" Port="11111" />
    <ProxyingGateway Address="localhost" Port="30000" />
  </Defaults>
</OrleansConfiguration>
```

Silo configuration via code is as follows.

``` c#
var config = ClusterConfiguration.LocalhostPrimarySilo(11111, 30000);
```

To connect to the local silo, the client needs to be configured to localhost and can only connect from the same machine. The Orleans client that can be started by running a project created with the *Orleans Dev/Test Host* template is configured as follows in DevTestClientConfiguration.xml

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <Gateway Address="localhost" Port="30000"/>
</ClientConfiguration>
```

Client configuration via code is as follows.

``` c#
var config = ClientConfiguration.LocalhostSilo(30000);
```

## Reliable Production Deployment Using Azure
For a reliable production deployment using Azure, you need to use the Azure Table option for cluster membership. This configuration is typical of deployments to either on-premise servers or Azure virtual machine instances.

 The format of the DataConnection string is "DefaultEndpointsProtocol=https;AccountName=<Azure storage account>;AccountKey=<Azure table storage account key>"


``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <SystemStore SystemStoreType="AzureTable"
         DeploymentId="<your deployment ID>"
         DataConnectionString="<<see comment above>>" />
    <Liveness LivenessType ="AzureTable" />
  </Globals>
  <Defaults>
    <Networking Address="" Port="11111" />
    <ProxyingGateway Address="" Port="30000" />
  </Defaults>
</OrleansConfiguration>
```

Clients need to be configured to use Azure Table for discovering the gateways, the addresses of the Orleans servers are not statically known to the clients.

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <SystemStore SystemStoreType="AzureTable" DeploymentId="target deployment ID" DataConnectionString="<<see comment above>>" />
</ClientConfiguration>
```

## Reliable Production Deployment Using ZooKeeper
For a reliable production deployment using ZooKeeper, you need to use the ZooKeeper option for cluster membership. This configuration is typical of deployments to on-premise servers.

 The format of the DataConnection string is documented in the [ZooKeeper Programmer's Guide](http://zookeeper.apache.org/doc/r3.4.6/zookeeperProgrammers.html#ch_zkSessions). A minimum of 5 ZooKeeper servers is [recommended](http://zookeeper.apache.org/doc/r3.4.6/zookeeperAdmin.html#sc_zkMulitServerSetup).


``` xml
<?xml version="1.0" encoding="utf-8"?>
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <SystemStore SystemStoreType="ZooKeeper"
                   DeploymentId="<your deployment ID>"
                   DataConnectionString="<<see comment above>>"/>
  </Globals>
  <Defaults>
    <Networking Address="localhost" Port="11111" />
    <ProxyingGateway Address="localhost" Port="30000" />
  </Defaults>
</OrleansConfiguration>
```

Clients need to be configured to use ZooKeeper for discovering the gateways, the addresses of the Orleans servers are not statically known to the clients.

``` xml
﻿<?xml version="1.0" encoding="utf-8" ?>
<ClientConfiguration xmlns="urn:orleans">
  <SystemStore SystemStoreType="ZooKeeper" DeploymentId="target deployment ID" DataConnectionString="<<see comment above>>"/>
</ClientConfiguration>
```

## Reliable Production Deployment Using SQL Server
For a reliable production deployment using SQL server, a SQL server connection string needs to be supplied.

Silo configuration via code is as follows, and includes logging configuration.

``` c#
var connectionString = @"Data Source=MSSQLDBServer;Initial Catalog=Orleans;Integrated Security=True;
    Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True";

var config = new ClusterConfiguration{    
    Globals =    
    {
        DataConnectionString = connectionString,
        DeploymentId = "<your deployment ID>",
        
        LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer,
        LivenessEnabled = true,
        ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.SqlServer
    },
    Defaults =
    {        
        Port = 11111,
        ProxyGatewayEndpoint = new IPEndPoint(address, 30000),
        PropagateActivityId = true
    }};
        
var siloHost = new SiloHost(System.Net.Dns.GetHostName(), config);
```

Clients need to be configured to use SQL server for discovering the gateways, as with Azure and Zookeeper, the addresses of the Orleans servers are not statically known to the clients.

``` c#

var connectionString = @"Data Source=MSSQLDBServer;Initial Catalog=Orleans;Integrated Security=True;
    Max Pool Size=200;Asynchronous Processing=True;MultipleActiveResultSets=True";

var config = new ClientConfiguration{
    GatewayProvider = ClientConfiguration.GatewayProviderType.SqlServer,
    AdoInvariant = "System.Data.SqlClient",
    DataConnectionString = connectionString,
    
    DeploymentId = "<your deployment ID>",    
    PropagateActivityId = true
};

var client = new ClientBuilder().UseConfiguration(config).Build();
await client.Connect();
```

## Unreliable Deployment on a Cluster of Dedicated Servers
For testing on a cluster of dedicated servers when reliability isn’t a concern you can leverage MembershipTableGrain and avoid dependency on Azure Table. You just need to designate one of the nodes as a Primary.

``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <SeedNode Address="<primary node>" Port="11111" />
    <Liveness LivenessType ="MembershipTableGrain" />
  </Globals>
  <Defaults>
    <Networking Address=" " Port="11111" />
    <ProxyingGateway Address=" " Port="30000" />
  </Defaults>
</OrleansConfiguration>
```

 For the client:

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <Gateway Address="node-1" Port="30000"/>
  <Gateway Address="node-2" Port="30000"/>
  <Gateway Address="node-3" Port="30000"/>
</ClientConfiguration>
```

## Azure Worker Role Deployment
When Orleans is deployed into an Azure Worker role, as opposed to VM instances, most of the server-side configuration is actually done in files other than the OrleansConfiguration, which looks something like this:


``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <Liveness LivenessType="AzureTable" />
  </Globals>
  <Defaults>
    <Tracing DefaultTraceLevel="Info" TraceToConsole="true" TraceToFile="{0}-{1}.log" />
  </Defaults>
</OrleansConfiguration>
```

 Some information is kept in the service configuration file, in which the worker role section looks like this:

``` xml
<Role name="OrleansAzureSilos">
  <Instances count="2" />
  <ConfigurationSettings>
    <Setting name="DataConnectionString" value="<<see earlier comment>>" />
    <Setting name="Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString" value="<<see earlier comment>>" />
  </ConfigurationSettings>
</Role>
```

The data connection string and the diagnostics connection string do not have to be the same.

Some configuration information is kept in the service definition file. The worker role has to be configured there, too:

``` xml
<WorkerRole name="OrleansAzureSilos" vmsize="Large">
  <Imports>
    <Import moduleName="Diagnostics" />
  </Imports>
  <ConfigurationSettings>
    <Setting name="DataConnectionString" />
  </ConfigurationSettings>
  <LocalResources>
    <LocalStorage name="LocalStoreDirectory" cleanOnRoleRecycle="false" />
  </LocalResources>
  <Endpoints>
    <InternalEndpoint name="OrleansSiloEndpoint" protocol="tcp" port="11111" />
    <InternalEndpoint name="OrleansProxyEndpoint" protocol="tcp" port="30000" />
  </Endpoints>
</WorkerRole>
```

That's it for the worker role hosting the Orleans runtime. However, when deploying to Azure, there is typically a front end of some sort, either a web site or a web service, since making the Orleans ports public is not a good idea. Therefore, the client configuration is configuration of the web or worker role (or web site) that sits in front of Orleans.

**Important Note** As of November 2017, there is a limitation in Azure Cloud Services which prevents firewall configuration of `InternalEndpoint`s if there is only 1 role in the Cloud Service. If you are connecting to your cloud service via a Virtual Network, you will have to scale your Cloud Services to two instances in order for the firewall rules to be created

 Assuming that the frontend is a web role, a simple ClientConfiguration file should be used:

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <Tracing DefaultTraceLevel="Info"
           TraceToConsole="true"
           TraceToFile="{0}-{1}.log"
           WriteTraces="false"/>
</ClientConfiguration>
```

 The web role needs the same connection string information as the worker role, in the service configuration file:

``` xml
<Role name="WebRole">
  <Instances count="2" />
  <ConfigurationSettings>
    <Setting name="DataConnectionString" value="<<see earlier comment>>" />
    <Setting name="Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString" value="<<see earlier comment>>" />
  </ConfigurationSettings>
</Role>
```

 and in the service definition file:

``` xml
<WebRole name="WebRole" vmsize="Large">
  <Imports>
    <Import moduleName="Diagnostics" />
  </Imports>
  <ConfigurationSettings>
    <Setting name="DataConnectionString" />
  </ConfigurationSettings>
  <!-- There is additional web role data that has nothing to do with Orleans -->
</WebRole>
```
