---
layout: page
title: Typical Configurations
---
{% include JB/setup %}

Below are examples of typical configurations that can be used for development and production deployments.

## Local Development 
For local development, where there is only one silo running locally on the programmer’s machine, the configuration is already included in the SDK. The local silo that can be started with the StartLocalSilo.cmd script in the top Orleans SDK folder is configured as follows.

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

To connect to the local silo, the client needs to be configured to localhost and can only connect from the same machine.

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <Gateway Address="localhost" Port="30000"/>
</ClientConfiguration>
```

## Reliable Production Deployment 
For a reliable production deployment, you need to use the Azure Table option for cluster membership. This configuration is typical of deployments to either on-premise servers or Azure virtual machine instances.

 The format of the DataConnection string is "DefaultEndpointsProtocol=https;AccountName=<Azure storage account>;AccountKey=<Azure table storage account key>"


``` xml
<OrleansConfiguration xmlns="urn:orleans">
  <Globals>
    <Liveness LivenessType ="AzureTable" />
    <Azure DeploymentId="<your deployment ID>" DataConnectionString="<<see comment above>>"/>
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
  <Azure DeploymentId="target deployment ID" DataConnectionString="<<see comment above>>"/>
</ClientConfiguration>
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
