---
layout: page
title: Grain Identity
---

# Grain Identity

In object-oriented environments, the identity of an object is hard to distinguish from a reference to it.
Thus, when an object is created using new, the reference you get back represents all aspects of its identity except those that map the object to some external entity that it represents.

In distributed systems, object references cannot represent instance identity, since references are typically limited to a single address space.
That is certainly the case for .NET references.
Furthermore, a virtual actor must have an identity regardless of whether it is active, so that we can activate it on demand.
Therefore grains have a primary key.
The primary key can be a Globally Unique Identifier (GUID), a long integer, or a string.

The primary key is scoped to the grain type.
Therefore, the complete identity of a grain is formed from the actor type and its key.

The caller of the grain decides a long, a GUID, or a string scheme should be used.
In fact the underlying data is the same, so the schemes can be used interchangeably.
When a long integer is used, a GUID is actually created and padded with zeros.

Situations that require a singleton grain instance, such as a dictionary or registry, benefit from using `Guid.Empty` as its key.
This is merely a convention, but by adhering, it becomes clear at the call site that it is what is going on, as we saw in the first tutorial:

## Using GUIDs

GUIDs are useful when there are several processes that could request a grain, such as a number of web servers in a web farm.
You don't need to coordinate the allocation of keys, which could introduce a single point of failure in the system, or a system-side lock on a resource which could present a bottleneck.
There is a very low chance of GUIDs colliding, so they would probably be the default choice when architecting an Orleans system.

Referencing a grain by GUID in client code:

``` csharp
var grain = grainFactory.GetGrain<IExample>(Guid.NewGuid());
```

Retrieving the primary key from grain code:

``` csharp
public override Task OnActivateAsync()
{
    Guid primaryKey = this.GetPrimaryKey();
    return base.OnActivateAsync();
}
```

## Using Longs

A long integer is also available, which would make sense if the grain is persisted to a relational database, where numerical indexes are preferred over GUIDs.

Referencing a grain by long integer in client code:

``` csharp
var grain = grainFactory.GetGrain<IExample>(1);
```

Retrieving the primary key form grain code:

``` csharp
public override Task OnActivateAsync()
{
    long primaryKey = this.GetPrimaryKeyLong();
    return base.OnActivateAsync();
}
```

## Using Strings

A string is also available.

Referencing a grain by String in client code:

``` csharp
var grain = grainFactory.GetGrain<IExample>("myGrainKey");
```

Retrieving the primary key form grain code:

``` csharp
public override Task OnActivateAsync()
{
    string primaryKey = this.GetPrimaryKeyString();
    return base.OnActivateAsync();
}
```
## Using Compound Primary Key

If you have a system that doesn't fit well with either GUIDs or longs, you can opt for a compound primary key which allows you to use a combination of a GUID or long and a string to reference a grain.

You can inherit your interface from 'IGrainWithGuidCompoundKey' or 'IGrainWithIntegerCompoundKey" interface like this:

``` csharp
public interface IExampleGrain : Orleans.IGrainWithIntegerCompoundKey
{
    Task Hello();
}
```

In client code, this adds a second argument to the `GetGrain` method on the grain factory.

``` csharp
var grain = grainFactory.GetGrain<IExample>(0, "a string!", null);
```

To access the compound key in the grain, we can call an overload on the `GetPrimaryKey` method:

``` csharp
public class ExampleGrain : Orleans.Grain, IExampleGrain
{
    public Task Hello()
    {
	string keyExtension;
        long primaryKey = this.GetPrimaryKey(out keyExtension);
        Console.WriteLine("Hello from " + keyExtension);
        return TaskDone.Done;
    }
}
```



