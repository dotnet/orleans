---
layout: page
title: Client Configuration
---

[!include[](../../../warning-banner.md)]

# Client Configuration

The key parameter that has to be configured for a client is the silo’s client gateway endpoint(s) to connect to. There are two ways to do that: manually configure one or more gateway endpoints or point the client to the Azure Table used by silos’ cluster membership. In the latter case the client automatically discovers what silos with client gateways enabled are available within the deployment, and adjusts its connections to the gateways as they join or leave the cluster. This option is reliable and recommended for production deployment.

## Fixed Gateway Configuration
A fixed set of gateways is specified in the ClientConfiguration.xml with one or more Gateway nodes:

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <Gateway Address="gateway1" Port="30000"/>
  <Gateway Address="gateway2" Port="30000"/>
  <Gateway Address="gateway3" Port="30000"/>
</ClientConfiguration>
```

 One gateway is generally enough. Multiple gateway connections help increase throughput and reliability of the system.

## Gateway Configuration Based on Cluster Membership
To configure the client to automatically find gateways from the silo cluster membership table, you need to specify the Azure Table or SQL Server connection string and the target deployment ID.


``` xml
<ClientConfiguration xmlns="urn:orleans">
  <SystemStore SystemStoreType="AzureTable"
               DeploymentId="target deployment ID"
               DataConnectionString="Azure storage connection string"/>
</ClientConfiguration>
```

 or

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <SystemStore SystemStoreType="SqlServer"
               DeploymentId="target deployment ID"
               DataConnectionString="SQL connection string"/>
</ClientConfiguration>
```

 or

``` xml
<ClientConfiguration xmlns="urn:orleans">
  <SystemStore SystemStoreType="ZooKeeper"
               DeploymentId="target deployment ID"
               DataConnectionString="ZooKeeper connection string"/>
</ClientConfiguration>
```


## Local Silo
For the local development/test configuration that uses a local silo, the client gateway should be configured to 'localhost.'


``` xml
<ClientConfiguration xmlns="urn:orleans">
  <Gateway Address="localhost" Port="30000"/>
</ClientConfiguration>
```

## Web Role Client in Azure
When the client is a web role running inside the same Azure deployment as the silo worker roles, all gateway address information is read from the OrleansSiloInstances table when OrleansAzureClient.Initialize() is called. The Azure storage connection string used to find the correct OrleansSiloInstances table is specified in the "DataConnectionString" setting defined in the service configuration for the deployment & role.


``` xml
<ServiceConfiguration  ...>
  <Role name="WebRole"> ...
    <ConfigurationSettings>
      <Setting name="DataConnectionString" value="DefaultEndpointsProtocol=https;AccountName=MYACCOUNTNAME;AccountKey=MYACCOUNTKEY" />
    </ConfigurationSettings>
  </Role>
  ...
</ServiceConfiguration>
```

Both the silo worker roles and web client roles need to be use the same Azure storage account in order to successfully discover each other successfully.

When using OrleansAzureClient.Initialize() and OrleansSiloInstances table for gateway address discovery, no additional gateway address info in required in the client config file. Typically the ClientConfiguration.xml file will only contain some minimal debug / tracing configuration settings, although even that is not required.


``` xml
<ClientConfiguration xmlns="urn:orleans">
  <Tracing DefaultTraceLevel="Info" >
    <TraceLevelOverride LogPrefix="Application" TraceLevel="Info" />
  </Tracing>
</ClientConfiguration>
```


Code-based client configuration. This is a reference only example and SHOULD NOT be used AS-IS - you may need to fine-tune client parameters for your specific environment.

``` csharp
var dataConnection = "DefaultEndpointsProtocol=https;AccountName=MYACCOUNTNAME;AccountKey=MYACCOUNTKEY";

var config = new ClientConfiguration
{
    // Some top level features
    GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable,
    ResponseTimeout = TimeSpan.FromSeconds(30),
    DeploymentId = RoleEnvironment.DeploymentId,
    DataConnectionString = dataConnection,
    PropagateActivityId = true,

    // Tracing
    DefaultTraceLevel = Severity.Info,
    TraceToConsole = false,
    TraceFilePattern = @"Client_{0}-{1}.log",
    //TraceFilePattern = "false", // Set it to false or none to disable file tracing, effectively it sets config.Defaults.TraceFileName = null;

    TraceLevelOverrides =
    {
        Tuple.Create("ComponentName", Severity.Warning),
    }
};

config.RegisterStreamProvider<AzureQueueStreamProvider>("AzureQueueStreams",
    new Dictionary<string, string>
    {
        { "PubSubType", "ExplicitGrainBasedAndImplicit" },
        { "DeploymentId", "orleans-streams" }, // This will be a prefix name of your Queues - so be careful and use string that is valid for queue name
        { "NumQueues", "4" },
        { "GetQueueMessagesTimerPeriod", "100ms" },
        { "DataConnectionString", dataConnection }
    });

config.RegisterStreamProvider<SimpleMessageStreamProvider>("SimpleMessagingStreams",
    new Dictionary<string, string>
    {
        { "PubSubType", "ExplicitGrainBasedAndImplicit" }
    });

IClusterClient client = null;
while (true)
{
    try
    {
        // Build a client and then connect it to the cluster.
        client = new ClientBuilder()
            .UseConfiguration(config)
            .ConfigureServices(
                services =>
                {
                    // Services can be provided to the client here. These services are made
                    // available via dependency injection.
                    // ConfigureServices can be called multiple times for a single
                    // ClientBuilder instance.
                })
            .Build();

        // Connect the client to the cluster. Once connection succeeds, the client will
        // maintain the connection, automatically reconnecting as necessary.
        await client.Connect().ConfigureAwait(false);
        break;
    }
    catch (Exception exception)
    {
        // If the connection attempt fails, the client instance must be disposed.
        client?.Dispose();

        // TODO: Log the exception.
        // TODO: Add a counter to break up an infinite cycle (circuit breaker pattern).
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
}

// Use the client.
// Note that clients can be shared between threads and are typically long-lived.
var user client.GetGrain<IUserGrain>("leeroy77jenkins@battle.net");
Console.WriteLine(await user.GetProfile());
```
