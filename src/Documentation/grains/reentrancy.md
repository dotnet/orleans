---
layout: page
title: Reentrancy
---
# Reentrancy

Grain activations are single-threaded and, by default, process each request from beginning to completion before the next request can begin processing. In some circumstances, it may be desirable for an activation to process other requests while one request is waiting for an asynchronous operation to complete. For this and other reasons, Orleans gives the developer some control over the request interleaving behavior. Multiple requests may be interleaved in the following cases:

* The grain class is marked as `[Reentrant]`
* The interface method is marked as `[AlwaysInterleave]`
* The requests within the same call chain
* The grain's *MayInterleave* predicate returns `true`

Each of those cases are discussed in the following sections.

## Reentrant grains

`Grain` implementation classes may be marked with the `[Reentrant]` attribute to indicate that different requests may be freely interleaved.

In other words, a reentrant activation may start executing another request while a previous request has not finished processing.
Execution is still limited to a single thread, so the activation is still executing one turn at a time, and each turn is executing on behalf of only one of the activation’s requests.

Reentrant grain code will never run multiple pieces of grain code in parallel (execution of grain code will always be single-threaded), but reentrant grains **may** see the execution of code for different requests interleaving. That is, the continuation turns from different requests may interleave.

For example, with the below pseudo-code, when Foo and Bar are 2 methods of the same grain class:

``` csharp
Task Foo()
{
    await task1;    // line 1
    return Do2();   // line 2
}

Task Bar()
{
    await task2;   // line 3
    return Do2();  // line 4
}
```

If this grain is marked `[Reentrant]`, the execution of Foo and Bar may interleave.

For example, the following order of execution is possible:

Line 1, line 3, line 2 and line 4. That is, the turns from different requests interleave.

If the grain was not reentrant, the only possible executions would be: line 1, line 2, line 3, line 4 OR: line 3, line 4, line 1, line 2 (new request cannot start before the previous one finished).

The main tradeoff in choosing between reentrant and non-reentrant grains is the code complexity to make interleaving work correctly and the difficulty to reason about it.

In a trivial case when the grains are stateless and the logic is simple, fewer (but not too few, so that all the hardware threads are used) reentrant grains should be in general slightly more efficient.

If the code is more complex, then a larger number of non-reentrant grains, even if slightly less efficient overall, should save you a lot of grief of figuring out non-obvious interleaving issues.

In the end answer will depend on the specifics of the application.

## Interleaving methods

Grain interface methods marked with `[AlwaysInterleave]` will be interleaved regardless of whether the grain is reentrant or not. Consider the following example:

``` csharp
public interface ISlowpokeGrain : IGrainWithIntegerKey
{
    Task GoSlow();

    [AlwaysInterleave]
    Task GoFast();
}

public class SlowpokeGrain : Grain, ISlowpokeGrain
{
    public async Task GoSlow()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
    }

    public async Task GoFast()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
    }
}
```

Now consider the call flow initiated by the following client request:

``` csharp
var slowpoke = client.GetGrain<ISlowpokeGrain>(0);

// A) This will take around 20 seconds
await Task.WhenAll(slowpoke.GoSlow(), slowpoke.GoSlow());

// B) This will take around 10 seconds.
await Task.WhenAll(slowpoke.GoFast(), slowpoke.GoFast(), slowpoke.GoFast());
```

Calls to `GoSlow` will not be interleaved, so the execution of the two `GoSlow()` calls will take around 20 seconds.
On the other hand, because `GoFast` is marked `[AlwaysInterleave]`, the three calls to it will be executed concurrently and will complete in approximately 10 seconds total instead of requiring at least 30 seconds to complete.

## Reentrancy within a call chain

In order to avoid deadlocks, the scheduler allows reentrancy within a given call chain. Consider the following example of two grains which have mutually recursive methods, `IsEven` and `IsOdd`:

``` csharp
public interface IEvenGrain : IGrainWithIntegerKey
{
    Task<bool> IsEven(int num);
}

public interface IOddGrain : IGrainWithIntegerKey
{
    Task<bool> IsOdd(int num);
}

public class EvenGrain : Grain, IEvenGrain
{
    public async Task<bool> IsEven(int num)
    {
        if (num == 0) return true;
        var oddGrain = this.GrainFactory.GetGrain<IOddGrain>(0);
        return await oddGrain.IsOdd(num - 1);
    }
}

public class OddGrain : Grain, IOddGrain
{
    public async Task<bool> IsOdd(int num)
    {
        if (num == 0) return false;
        var evenGrain = this.GrainFactory.GetGrain<IEvenGrain>(0);
        return await evenGrain.IsEven(num - 1);
    }
}
```

Now consider the call flow initiated by the following client request:

``` csharp
var evenGrain = client.GetGrain<IEvenGrain>(0);
await evenGrain.IsEven(2);
```

The above code calls `IEvenGrain.IsEven(2)`, which calls `IOddGrain.IsOdd(1)`, which calls `IEvenGrain.IsEven(0)`, which returns `true` back up the call chain to the client. Without call chain reentrancy, the above code will result in a deadlock when `IOddGrain` calls `IEvenGrain.IsEven(0)`. With call chain reentrancy, however, the call is allowed to proceed as it is deemed to be the intention of the developer.

This behavior can be disabled by setting `SchedulingOptions.AllowCallChainReentrancy` to `false`. For example:

``` csharp
siloHostBuilder.Configure<SchedulingOptions>(
    options => options.AllowCallChainReentrancy = false);
```

## Reentrancy using a predicate

Grain classes can specify a predicate used to determine interleaving on a call-by-call basis by inspecting the request. The `[MayInterleave(string methodName)]` attribute provides this functionality. The argument to the attribute is the name of a static method within the grain class which accepts an `InvokeMethodRequest` object and returns a `bool` indicating whether or not the request should be interleaved.

Here is an example which allows interleaving if the request argument type has the `[Interleave]` attribute:

``` csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class InterleaveAttribute : Attribute { }

// Specify the may-interleave predicate.
[MayInterleave(nameof(ArgHasInterleaveAttribute))]
public class MyGrain : Grain, IMyGrain
{
    public static bool ArgHasInterleaveAttribute(InvokeMethodRequest req)
    {
        // Returning true indicates that this call should be interleaved with other calls.
        // Returning false indicates the opposite.
        return req.Arguments.Length == 1
            && req.Arguments[0]?.GetType().GetCustomAttribute<InterleaveAttribute>() != null;
    }

    public Task Process(object payload)
    {
        // Process the object.
    }
}
```
