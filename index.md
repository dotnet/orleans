---
layout: index
title: Microsoft Orleans
tagline: A straightforward approach to building distributed, high-scale applications in .NET
---
{% include JB/setup %}

Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 
It was created by Microsoft Research and designed for use in the cloud. 

Orleans has been used extensively in Microsoft Azure by several Microsoft product groups, most notably by 343 Industries as a platform for all of Halo 4 and Halo 5 cloud services, as well as by a growing number of other companies.

---

<div class="row">
    <div class="col-md-4">
        <h3>Scalable by Default</h3>
        
        Orleans handles the complexity of building distributed systems, enabling your application 
        to scale to hundreds of servers.
    </div>
    <div class="col-md-4">
        <h3>Low Latency</h3>
        
        Orleans allows you to keep the state you need in memory, so your application can rapidly respond
        to incoming requests.
    </div>
    <div class="col-md-4">
        <h3>Simplified Concurrency</h3> 
        
        Orleans allows you to write simple, single threaded C# code, handling concurrency with asynchronous 
        message passing between actors. 
    </div>
</div>

---

In Orleans, actors are called 'grains', and are described using an interface. Async methods are used to indicate which messages the actor can receive:

``` csharp
public interface IMyGrain : IGrainWithStringKey
{
    Task<string> SayHello(string name);
}
```

The implementation is executed inside the Orleans framework: 

``` csharp
public class MyGrain : IMyGrain
{
    public async Task<string> SayHello(string name)
    {
        return "Hello " + name;
    }
}
```

You can then send messages to the grain by creating a proxy object, and calling the methods:

``` csharp
var grain = GrainClient.GrainFactory.GetGrain<IMyGrain>("grain1");
await grain.SayHello("World");
```

## Where Next?

To learn more about the concepts in Orleans, read the [introduction](Introduction).

There are a number of [step-by-step tutorials](Step-by-step-Tutorials).

Discuss your Orleans questions on the [gitter chat room](https://gitter.im/dotnet/orleans).

Fork the code on the [GitHub Respository](https://github.com/dotnet/orleans).


