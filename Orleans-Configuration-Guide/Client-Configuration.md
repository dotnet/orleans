---
layout: page
title: Client Configuration
---
{% include JB/setup %}

The key parameter that has to be configured for a client is the silo’s client gateway endpoint(s) to connect to. There are two ways to do that: manually configure one or more gateway endpoints or point the client to the Azure Table used by silos’ cluster membership. In the latter case the client automatically discovers what silos with client gateways enabled are available within the deployment, and adjusts its connections to the gateways as they join or leave the cluster. This option is reliable and recommended for production deployment.

## Fixed Gateway Configuration 
A fixed set of gateways is specified in the ClientConfiguration.xml with one or more Gateway nodes:

    <ClientConfiguration xmlns="urn:orleans">
        <Gateway Address="gateway1" Port="30000"/>
        <Gateway Address="gateway2" Port="30000"/>
        <Gateway Address="gateway3" Port="30000"/>
    </ClientConfiguration>


 One gateway is generally enough. Multiple gateway connections help increase throughput and reliability of the system.

## Gateway Configuration Based on Cluster Membership
To configure the client to automatically find gateways from the silo cluster membership table, you need to specify the Azure Table or SQL Server connection string and the target deployment ID.


    <ClientConfiguration xmlns="urn:orleans">
        <SystemStore SystemStoreType="AzureTable"
                     DeploymentId="target deployment ID"
                     DataConnectionString="Azure storage connection string"/>
    </ClientConfiguration>

 or 

    <ClientConfiguration xmlns="urn:orleans">
        <SystemStore SystemStoreType="SqlServer"
                     DeploymentId="target deployment ID"
                     DataConnectionString="SQL connection string"/>
    </ClientConfiguration>



## Local Silo
For the local development/test configuration that uses a local silo, the client gateway should be configured to 'localhost.'


    <ClientConfiguration xmlns="urn:orleans">
        <Gateway Address="localhost" Port="30000"/>
    </ClientConfiguration>


## Web Role Client in Azure
When the client is a web role running inside the same Azure deployment as the silo worker roles, all gateway address information is read from the OrleansSiloInstances table when OrleansAzureClient.Initialize() is called. The Azure storage connection string used to find the correct OrleansSiloInstances table is specified in the "DataConnectionString" setting defined in the service configuration for the deployment & role. 


    <ServiceConfiguration  ...>
        <Role name="WebRole"> ...
            <ConfigurationSettings>
                <Setting name="DataConnectionString" value="DefaultEndpointsProtocol=https;AccountName=MYACCOUNTNAME;AccountKey=MYACCOUNTKEY" />
            </ConfigurationSettings>
        </Role>
        ... 
    </ServiceConfiguration>


Both the silo worker roles and web client roles need to be use the same Azure storage account in order to successfully discover each other successfully.

When using OrleansAzureClient.Initialize() and OrleansSiloInstances table for gateway address discovery, no additional gateway address info in required in the client config file. Typically the ClientConfiguration.xml file will only contain some minimal debug / tracing configuration settings, although even that is not required.


    <ClientConfiguration xmlns="urn:orleans">
        <Tracing DefaultTraceLevel="Info" >
            <TraceLevelOverride LogPrefix="Application" TraceLevel="Info" />
        </Tracing>
    </ClientConfiguration>

