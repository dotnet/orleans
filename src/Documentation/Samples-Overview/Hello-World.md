---
layout: page
title: Hello World
---

# Hello World

## Run the Hello World sample
One way to run this sample is to download a local copy of HelloWorld from [the Samples/2.0/HelloWorld/ folder](https://github.com/dotnet/orleans/tree/master/Samples/2.0/HelloWorld/).

Open two Command Prompt windows and navigate to the HelloWorld folder in each one.

Build the project.

Start the silo in one window with the command:
```
dotnet run --project src\SiloHost
```
After the silo is running, start the client in the other window with this: 
```
dotnet run --project src\OrleansClient
```
The silo and client windows will display greetings to each other.

## How Orleans says Hello
In this sample, a client connects with a grain, sends it a greeting and receives a greeting back.
The client then prints that greeting and that's that.
Simple enough in theory, but since there's distribution involved, there's a bit more to it.

There are four projects involved -- one for declaring the grain interfaces, one for the grain implementations, and one for the client, one for the silo host

There's one grain interface, in IHello.cs:

``` csharp
public interface IHello : Orleans.IGrainWithIntegerKey
{
   Task<string> SayHello(string greeting);
}
```

This is simple enough, and we can see that all replies must be represented as a Task or Task<T> in communication interfaces.
The implementation, found in HelloGrain.cs, is similarly trivial:

``` csharp
public class HelloGrain : Orleans.Grain, HelloWorldInterfaces.IHello
{
    Task<string> HelloWorldInterfaces.IHello.SayHello(string greeting)
    {
        return Task.FromResult($"You said: '{greeting}', I say: Hello!");
    }
}
```

The class inherits from the base class `Grain`, and implements the communication interface defined earlier.
Since there is nothing that the grain needs to wait on, the method is not declared `async` and instead returns its value using `Task.FromResult()`.

 The client, which orchestrates the grain code and is found in OrleansClient project, looks like this:

``` csharp
//configure the client with proper cluster options, logging and clustering
 client = new ClientBuilder()
   .UseLocalhostClustering()
   .Configure<ClusterOptions>(options =>
   {
       options.ClusterId = "dev";
       options.ServiceId = "HelloWorldApp";
   })
   .ConfigureLogging(logging => logging.AddConsole())
   .Build();

//connect the client to the cluster, in this case, which only contains one silo
await client.Connect();
...
// example of calling grains from the initialized client
var friend = client.GetGrain<IHello>(0);
var response = await friend.SayHello("Good morning, my friend!");
Console.WriteLine("\n\n{0}\n\n", response);
```

The silo host, which configures and starts the silo, in SiloHost project looks like this:

``` csharp
 //define the cluster configuration
var builder = new SiloHostBuilder()
//configure the cluster with local host clustering
    .UseLocalhostClustering()
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "dev";
        options.ServiceId = "HelloWorldApp";
    })
    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
    .ConfigureLogging(logging => logging.AddConsole());
//build the silo
var host = builder.Build();
//start the silo
await host.StartAsync();
```


