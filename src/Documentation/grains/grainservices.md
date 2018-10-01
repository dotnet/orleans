---
layout: page
title: GrainServices
---

# GrainServices

A GrainService is a special grain; one that has no identity, and runs in every silo from startup to shutdown.

## Creating a GrainService

**Step 1.** Create the interface.
The interface of a GrainService is built using exactly the same principles you would use for building the interface of any other grain.

``` csharp
public interface IDataService : IGrainService {
    Task MyMethod();
}
```

**Step 2.** Create the DataService grain itself.
If possible, make the GrainService reentrant for better performance.
Note the necessary base constructor call.
It’s good to know that you can also inject an `IGrainFactory` so you can make grain calls from your GrainService.

A note about streams: a GrainService cannot write to Orleans streams because it doesn’t work within a grain task scheduler.
If you need the GrainService to write to streams for you, then you will have to send the object to another kind of grain for writing to the stream.

``` csharp
[Reentrant]

public class LightstreamerDataService : GrainService, IDataService {

    readonly IGrainFactory GrainFactory;

    public LightstreamerDataService(IServiceProvider services, IGrainIdentity id, Silo silo, ILoggerFactory loggerFactory, IGrainFactory grainFactory) : base(id, silo, loggerFactory) {
        GrainFactory = grainFactory;
    }

    public override Task Init(IServiceProvider serviceProvider) {
        return base.Init(serviceProvider);
    }

    public override async Task Start() {
        await base.Start();
    }

    public override Task Stop() {
        return base.Stop();
    }

    public Task MyMethod() {
 }
}
```

**Step 3.** Create an interface for the GrainServiceClient to be used by other grains to connect to the GrainService.

``` csharp
public interface IDataServiceClient : IGrainServiceClient<IDataService>, IDataService {
}
```

**Step 4.** Create the actual grain service client.
It pretty much just acts as a proxy for the data service.
Unfortunately, you have to manually type in all the method mappings, which are just simple one-liners.

``` csharp
public class DataServiceClient : GrainServiceClient<IDataService>, IDataServiceClient {

    public DataServiceClient(IServiceProvider serviceProvider) : base(serviceProvider) {
    }

    public Task MyMethod()  => GrainService.MyMethod();
}
```

**Step 5.** Inject the grain service client into the other grains that need it.
Note that the GrainServiceClient does not guarantee accessing the GrainService on the local silo.
Your command could potentially be sent to the GrainService on any silo in the cluster.

``` csharp
public class MyNormalGrain: Grain<NormalGrainState>, INormalGrain {

    readonly IDataServiceClient DataServiceClient;

    public MyNormalGrain(IGrainActivationContext grainActivationContext, IDataServiceClient dataServiceClient) {
                DataServiceClient = dataServiceClient;
    }
}
```

**Step 6.** Inject the grain service into the silo itself.
You need to do this so that the silo will start the GrainService.

``` csharp
(ISiloHostBuilder builder) => builder .ConfigureServices(services => { services.AddSingleton<IDataService, DataService>(); });

```

## Additional Notes

###Note 1

There's an extension method on `ISiloHostBuilder: AddGrainService<SomeGrainService>()`.
Type constraint is: `where T : GrainService`.
It ends up calling this bit: **orleans/src/Orleans.Runtime/Services/GrainServicesSiloBuilderExtensions.cs**

 `return services.AddSingleton<IGrainService>(sp => GrainServiceFactory(grainServiceType, sp));`

Basically, the silo fetches `IGrainService` types from the service provider when starting: **orleans/src/Orleans.Runtime/Silo/Silo.cs**
 `var grainServices = this.Services.GetServices<IGrainService>();`

The `Orleans.Runtime` Nuget package should be imported into the Grainservice base class.
 
###Note 2
 
In order for this to work you have to register both the Service and its Client.
The code looks something like this:
``` csharp
  var builder = new SiloHostBuilder()
      .AddGrainService<DataService>()  // Register GrainService
      .ConfigureServices(s =>
       {
          // Register Client of GrainService
          s.AddSingleton<IDataServiceClient, DataServiceClient>(); 
      })
 ```
 