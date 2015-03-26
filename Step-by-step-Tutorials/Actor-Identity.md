---
layout: page
title: Actor Identity
---
{% include JB/setup %}

In object-oriented environments, the identity of an object is hard to distinguish from a reference to it. Thus, when an object is created using new, the reference you get back represents all aspects of its identity except those that map the object to some external entity that it represents.

 In distributed systems, object references cannot represent instance identity, since references are typically limited to a single address space. That is certainly the case for .NET references. Furthermore, a virtual actor must have an identity regardless of whether it is active, so that we can activate it on demand. Therefore grains have a primary key. The primary key can be either a GUID (A Globally Unique Identifier) or a long integer.

 The primary key is scoped to the grain type. Therefore, the complete identity of a grain is formed from the actor type and its key. 

 The caller of the grain decides whether a long or a GUID scheme should be used. In fact the underlying data is the same, so the schemes can be used interchangeably. When a long is used, a GUID is actually created, and padded with zeros.

 Situations that require a singleton grain instance, such as a dictionary or registry, benefit from using '0' (a valid GUID) as its key. This is merely a convention, but by adhering, it becomes clear at the call site that it is what is going on, as we saw in the first tutorial:

## Using GUIDs

GUIDs are useful when there are several processes that could request a grain, such as a number of web servers in a web farm. You don't need to coordinate the allocation of keys, which could introduce a single point of failure in the system, or a system-side lock on a resource which could present a bottleneck. There is a very low chance of GUIDs colliding, so they would probably be the default choice when architecting an Orleans system. 

 Referencing a grain by GUID in client code:

``` csharp
var grain = ExampleGrainFactor.GetGrain(Guid.NewGuid());
```

Retrieving the primary key form grain code:

``` csharp
public override Task ActivateAsync()
{
    Guid primaryKey = this.GetPrimaryKey();
    return base.ActivateAsync();
}
```

## Using Longs

A long integer is also available, which would make sense if the grain is persisted to a relational database, where numerical indexes are preferred over GUIDs.

 Referencing a grain by GUID in client code:

``` csharp
var grain = ExampleGrainFactor.GetGrain(1);
```

Retrieving the primary key form grain code:

``` csharp
public override Task ActivateAsync()
{
    long primaryKey = this.GetPrimaryKeyLong();
    return base.ActivateAsync();
}
```

## Using Extended Primary Key

If you have a system that doesn't fit well with either GUIDs or longs, you can opt for an extended primary key which allows you to use a string to reference a grain.

 You can mark a grain interface with an [ExtendedPrimaryKey] attribute like this:

``` csharp
[ExtendedPrimaryKey]
public interface IExampleGrain : Orleans.IGrain
{
    Task Hello();
}
```

In client code, this adds a second argument to the GetGrain method on the grain factory.

``` csharp
var grain = ExampleGrainFactory.GetGrain(0, "a string!");
```

Notice we still have a primary key, which can still be either a GUID or a long. We can choose to ignore this by setting the primary key to zero, or we can combine the primary key and secondary key together as a joined key.

 To access the extended key in the grain, we can call an overload on the  GetPrimaryKey method:

``` csharp
public class ExampleGrain : Orleans.GrainBase, IExampleGrain
{
    public Task Hello()
    {
	    string extendedKey;
        long primaryKey = this.GetPrimaryKey(out extendedKey);
        Console.WriteLine("Hello from " + extendedKey);
        return TaskDone.Done;
    }
}
```

The stock ticker example used in the  Interaction with Libraries and Services uses ExtendedPrimaryKey to activate grains representing different stock symbols.

## Next

Let's add another type of grain into the solution, and demonstrate inter-grain communication.

[A Service is a Collection of Communicating Actors](A-Service-is-a-Collection-of-Communicating-Actors)
