---
layout: page
title: Migration from Orleans 1.5 to 2.0 when using Azure
---

# Migration from Orleans 1.5 to 2.0 when using Azure

Since the move to 2.0, the configuration of the silo has changed, before we used to have a major object that handled all the configuration steps, then the developer was able to add each provider as needed, now every config step is based on a Silo Builder, similar to how it is done in ASP.NET Core with the WebHostBuilder.

On 1.5.x, the configuration for Azure looked like this:
```csharp
    var config = AzureSilo.DefaultConfiguration();
    config.AddMemoryStorageProvider();
    config.AddAzureTableStorageProvider("AzureStore", RoleEnvironment.GetConfigurationSettingValue("DataConnectionString"));
```

The AzureSilo have a static method named DefaultConfiguration(), this method used to load everything from the configuration file of the service. Now, you must load everything manually, the new model is not to depend on some sort of naming convention, you can name your endpoints as you want, so the new configuration API  looks like this:

```csharp
    //Load the different settings from the services configuration file
    var proxyPort = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["OrleansProxyEndpoint"].IPEndpoint.Port;
    var siloEndpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["OrleansSiloEndpoint"].IPEndpoint;
    var connectionString = RoleEnvironment.GetConfigurationSettingValue("DataConnectionString");
    var deploymentId = RoleEnvironment.DeploymentId;


    var builder = new SiloHostBuilder()
        //Now we set the cluster ID
        .Configure(config => config.ClusterId = deploymentId)
        .Configure<ClusterOptions>(config => config.ServiceId = deploymentId)
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
Before, the AzureSilo class was available to run the silo, now everything runs on a ISiloHost. The way to create a silo before 2.0 was the following

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

Now, it works as follows
```csharp

    public class WorkerRole : RoleEntryPoint
    {
        private ISiloHost host;
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
            => base.OnStart();

        public override void OnStop()
        {
            this.cancellationTokenSource.Cancel();
            this.runCompleteEvent.WaitOne();

            host.StopAsync().Wait();

            base.OnStop();
        }

        private Task RunAsync(CancellationToken cancellationToken)
        {
            //builder is the SiloHostBuilder from the first section
            host = builder.Build();

            return host.StartAsync(cancellationToken);
        }
    }
```