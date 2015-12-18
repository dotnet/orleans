---
layout: index
title: Microsoft Project Orleans
tagline: A straightforward approach to building distributed, high-scale applications in .NET
---
{% include JB/setup %}

Orleans is a framework that provides a straightforward approach to building distributed high-scale computing applications, without the need to learn and apply complex concurrency or other scaling patterns. 
It was created by Microsoft Research and designed for use in the cloud. 

Orleans has been used extensively in Microsoft Azure by several Microsoft product groups, most notably by 343 Industries as a platform for all of Halo 4 and Halo 5 cloud services, as well as by a number of other companies.

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
grain.SayHello("World");
```
