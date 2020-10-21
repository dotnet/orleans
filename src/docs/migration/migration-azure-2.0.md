---
layout: page
title: Migration from Orleans 1.5 to 2.0 when using Azure
---

# Migration from Orleans 1.5 to 2.0 when using Azure

In Orleans 2.0, the configuration of silos and clients has changed.
In Orleans 1.5 we used to have a monolith object that handled all the configuration pieces
Providers were added to that configuration object, too.
In Orleans 2.0, the configuration process is organizes around `SiloHostBuilder`, similar to how it is done in ASP.NET Core with the `WebHostBuilder`.

In Orleans 1.5, the configuration for Azure looked like this:
```csharp
    var config = AzureSilo.DefaultConfiguration();
    config.AddMemoryStorageProvider();
    config.AddAzureTableStorageProvider("AzureStore", RoleEnvironment.GetConfigurationSettingValue("DataConnectionString"));
```

The `AzureSilo` class exposes a static method named DefaultConfiguration() that was used for loading configuration XML file.
This way of configuring a silo is deprecated but still supported via the [legacy support package](https://www.nuget.org/packages/Microsoft.Orleans.Core.Legacy/).

In Orleans 2.0, configuration is completely programmatic.
The new configuration API  looks like this:

```csharp
    //Load the different settings from the services configuration
    var proxyPort = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["OrleansProxyEndpoint"].IPEndpoint.Port;
    var siloEndpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["OrleansSiloEndpoint"].IPEndpoint;
    var connectionString = RoleEnvironment.GetConfigurationSettingValue("DataConnectionString");
    var deploymentId = RoleEnvironment.DeploymentId;


    var builder = new SiloHostBuilder()
        //Set service ID and cluster ID
        .Configure<ClusterOptions>(options => 
            {
                options.ClusterId = deploymentId;
                options.ServiceIs = "my-app";
            })
        // Set silo name
        .Configure<SiloOptions>(options => options.SiloName = this.Name)
        //Then, we can configure the different endpoints
        .ConfigureEndpoints(siloEndpoint.Address, siloEndpoint.Port, proxyPort)
        //Then, we set the connection string for the storage
        .UseAzureStorageClustering(options => options.ConnectionString = connectionString)
        //If reminders are needed, add the service, the connection string is required
        .UseAzureTableReminderService(connectionString)
        //If Queues are needed, add the service, set the name and the Adapter, the one shown here
        //is the one provided with Orleans, but it can be a custom one
        .AddAzureQueueStreams<AzureQueueDataAdapterV2>("StreamProvider",
            configurator => configurator.Configure(configure =>
            {
                configure.ConnectionString = connectionString;
            }))
        //If Grain Storage is needed, add the service and set the name
        .AddAzureTableGrainStorage("AzureTableStore");
```

# AzureSilo to ISiloHost
In Orleans 1.5, the `AzureSilo` class was the recommended way to host a silo in an Azure Worker Role.
This is still supported via the [`Microsoft.Orleans.Hosting.AzureCloudServices` NuGet package](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.AzureCloudServices/).

```csharp
public class WorkerRole : RoleEntryPoint
{
    AzureSilo silo;

    public override bool OnStart()
    {
        // Do other silo initialization – for example: Azure diagnostics, etc
        return base.OnStart();
    }

    public override void OnStop()
    {
        silo.Stop();
        base.OnStop();
    }

    public override void Run()
    {
        var config = AzureSilo.DefaultConfiguration();
        config.AddMemoryStorageProvider();
        config.AddAzureTableStorageProvider("AzureStore", RoleEnvironment.GetConfigurationSettingValue("DataConnectionString"));

        // Configure storage providers
        silo = new AzureSilo();
        bool ok = silo.Start(config);

        silo.Run(); // Call will block until silo is shutdown
    }
}
```

Orleans 2.0 provides a more flexible and modular API for configuring and hosting a silo via `SiloHostBuilder` and `ISiloHost`.

```csharp

    public class WorkerRole : RoleEntryPoint
    {
        private ISiloHost host;
        private ISiloHostBuilder builder;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent runCompleteEvent = new ManualResetEvent(false);

        public override void Run()
        {
            try
            {
                this.RunAsync(this.cancellationTokenSource.Token).Wait();
                runCompleteEvent.WaitOne();
            }
            finally
            {
                this.runCompleteEvent.Set();
            }
        }

        public override bool OnStart()
        {
            //builder is the SiloHostBuilder from the first section
            // Build silo host, so that any errors will restart the role instance
            this.host = this.builder.Build();

            return base.OnStart();
        }

        public override void OnStop()
        {
            this.cancellationTokenSource.Cancel();
            this.runCompleteEvent.WaitOne();

            this.host.StopAsync().Wait();

            base.OnStop();
        }

        private Task RunAsync(CancellationToken cancellationToken)
        {
            return this.host.StartAsync(cancellationToken);
        }
    }
```
