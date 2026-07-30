---
title: Request scheduling
description: Learn about request scheduling in .NET Orleans.
ms.date: 03/09/2026
ms.topic: concept-article
ai-usage: ai-assisted
---

# Request scheduling

Grain activations have a *single-threaded* execution model. By default, they process each request from beginning to completion before the next request can begin processing. In some circumstances, it might be desirable for an activation to process other requests while one request waits for an asynchronous operation to complete. For this and other reasons, Orleans gives you some control over the request interleaving behavior, as described in the [Reentrancy](#reentrancy) section. What follows is an example of non-reentrant request scheduling, which is the default behavior in Orleans.

Consider the following `PingGrain` definition:

```csharp
public interface IPingGrain : IGrainWithStringKey
{
    Task Ping();
    Task CallOther(IPingGrain other);
}

public class PingGrain : Grain, IPingGrain
{
    private readonly ILogger<PingGrain> _logger;

    public PingGrain(ILogger<PingGrain> logger) => _logger = logger;

    public Task Ping() => Task.CompletedTask;

    public async Task CallOther(IPingGrain other)
    {
        _logger.LogInformation("1");
        await other.Ping();
        _logger.LogInformation("2");
    }
}
```

Two grains of type `PingGrain` are involved in our example, *A* and *B*. A caller invokes the following call:

```csharp
var a = grainFactory.GetGrain("A");
var b = grainFactory.GetGrain("B");
await a.CallOther(b);
```

:::image type="content" source="grain-persistence/media/reentrancy-scheduling-diagram.png" alt-text="Reentrancy scheduling diagram." lightbox="grain-persistence/media/reentrancy-scheduling-diagram.png":::

The flow of execution is as follows:

1. The call arrives at *A*, which logs `"1"` and then issues a call to *B*.
1. *B* returns immediately from `Ping()` back to *A*.
1. *A* logs `"2"` and returns back to the original caller.

While *A* awaits the call to *B*, it can't process any incoming requests. As a result, if *A* and *B* were to call each other simultaneously, they might *deadlock* while waiting for those calls to complete. Here's an example, based on the client issuing the following call:

```csharp
var a = grainFactory.GetGrain("A");
var b = grainFactory.GetGrain("B");

// A calls B at the same time as B calls A.
// This might deadlock, depending on the non-deterministic timing of events.
await Task.WhenAll(a.CallOther(b), b.CallOther(a));
```

## Case 1: The calls don't deadlock

:::image type="content" source="grain-persistence/media/reentrancy-scheduling-diagram-01.png" alt-text="Reentrancy scheduling diagram without deadlock." lightbox="grain-persistence/media/reentrancy-scheduling-diagram-01.png":::

In this example:

1. The `Ping()` call from *A* arrives at *B* before the `CallOther(a)` call arrives at *B*.
1. Therefore, *B* processes the `Ping()` call before the `CallOther(a)` call.
1. Because *B* processes the `Ping()` call, *A* is able to return back to the caller.
1. When *B* issues its `Ping()` call to *A*, *A* is still busy logging its message (`"2"`), so the call has to wait for a short duration, but it's soon able to be processed.
1. *A* processes the `Ping()` call and returns to *B*, which returns to the original caller.

Consider a less fortunate series of events where the same code results in a *deadlock* due to slightly different timing.

## Case 2: The calls deadlock

:::image type="content" source="grain-persistence/media/reentrancy-scheduling-diagram-02.png" alt-text="Reentrancy scheduling diagram with deadlock." lightbox="grain-persistence/media/reentrancy-scheduling-diagram-02.png":::

In this example:

1. The `CallOther` calls arrive at their respective grains and are processed simultaneously.
1. Both grains log `"1"` and proceed to `await other.Ping()`.
1. Since both grains are still *busy* (processing the `CallOther` request, which hasn't finished yet), the `Ping()` requests wait.
1. After a while, Orleans determines that the call has **timed out**, and each `Ping()` call results in an exception being thrown.
1. The `CallOther` method body doesn't handle the exception, and it bubbles up to the original caller.

The following section describes how to prevent deadlocks by allowing multiple requests to interleave their execution.

## Reentrancy

> [!WARNING]
> Reentrancy is an advanced feature that requires a solid understanding of concurrency concepts. Incorrect use can lead to race conditions, state corruption, or subtle bugs that are difficult to diagnose. Ensure you understand the implications before enabling reentrancy on your grains.

Orleans defaults to a safe execution flow where the internal state of a grain isn't modified concurrently by multiple requests. Concurrent modification complicates logic and places a greater burden on you, the developer. This protection against concurrency bugs has a cost, primarily *liveness*: certain call patterns can lead to deadlocks, as discussed previously. One way to avoid deadlocks is to ensure grain calls never form a cycle. Often, it's difficult to write code that is cycle-free and guaranteed not to deadlock. Waiting for each request to run from beginning to completion before processing the next can also hurt performance. For example, by default, if a grain method performs an asynchronous request to a database service, the grain pauses request execution until the database response arrives.

Each of these cases is discussed in the following sections. For these reasons, Orleans provides options to allow some or all requests to execute *concurrently*, interleaving their execution. In Orleans, we refer to such concerns as *reentrancy* or *interleaving*. By executing requests concurrently, grains performing asynchronous operations can process more requests in a shorter period.

Multiple requests may be interleaved in the following cases:

- The grain class is marked with <xref:Orleans.Concurrency.ReentrantAttribute>.
- The interface method is marked with <xref:Orleans.Concurrency.AlwaysInterleaveAttribute>.
- The grain's <xref:Orleans.Concurrency.MayInterleaveAttribute> predicate returns `true`.

The following table summarizes all available reentrancy options:

| Option | Scope | Description |
|--------|-------|-------------|
| <xref:Orleans.Concurrency.ReentrantAttribute> | Grain class | All methods in the grain can freely interleave with each other. |
| <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> | Interface method | The marked method always interleaves with any other request, and any other request can interleave with it. |
| <xref:Orleans.Concurrency.ReadOnlyAttribute> | Interface method | The method doesn't modify grain state and can run concurrently with other <xref:Orleans.Concurrency.ReadOnlyAttribute> methods. |
| <xref:Orleans.Concurrency.MayInterleaveAttribute> | Grain class | A predicate method determines on a per-call basis whether a specific request should interleave. |
| <xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy> | Call site | Allows reentrancy for the duration of the call chain scoped to callers further down the chain, giving fine-grained control over where reentrancy is enabled. |

With reentrancy, the following case becomes a valid execution, removing the possibility of the deadlock described above.

### Case 3: The grain or method is reentrant

:::image type="content" source="grain-persistence/media/reentrancy-scheduling-diagram-03.png" alt-text="Re-entrancy scheduling diagram with re-entrant grain or method." lightbox="grain-persistence/media/reentrancy-scheduling-diagram-03.png":::

In this example, grains *A* and *B* can call each other simultaneously without potential request scheduling deadlocks because both grains are *reentrant*. The following sections provide more details on reentrancy.

### Reentrant grains

You can mark <xref:Orleans.Grain> implementation classes with the <xref:Orleans.Concurrency.ReentrantAttribute> to indicate that different requests can be freely interleaved.

In other words, a reentrant activation might start processing another request while a previous request hasn't finished. Execution is still limited to a single thread, so the activation executes one turn at a time, and each turn executes on behalf of only one of the activation's requests.

Reentrant grain code never runs multiple pieces of grain code in parallel (execution is always single-threaded), but reentrant grains **might** see the execution of code for different requests interleaving. That is, the continuation turns from different requests might interleave.

For example, as shown in the following pseudo-code, consider that `Foo` and `Bar` are two methods of the same grain class:

```csharp
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

If this grain is marked <xref:Orleans.Concurrency.ReentrantAttribute>, the execution of `Foo` and `Bar` might interleave.

For example, the following order of execution is possible:

Line 1, line 3, line 2 and line 4.
That is, the turns from different requests interleave.

If the grain wasn't re-entrant, the only possible executions would be: line 1, line 2, line 3, line 4 OR: line 3, line 4, line 1, line 2 (a new request can't start before the previous one finished).

The main tradeoff when choosing between reentrant and non-reentrant grains is the code complexity required to make interleaving work correctly and the difficulty of reasoning about it.

In a trivial case where grains are stateless and the logic is simple, using fewer reentrant grains (but not too few, ensuring all hardware threads are utilized) should generally be slightly more efficient.

If the code is more complex, using a larger number of non-reentrant grains, even if slightly less efficient overall, might save you significant trouble in debugging non-obvious interleaving issues.

In the end, the answer depends on the specifics of the application.

### Interleaving methods

Grain interface methods marked with <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> always interleave any other request and can always be interleaved by any other request, even requests for non-<xref:Orleans.Concurrency.AlwaysInterleaveAttribute> methods.

Consider the following example:

```csharp
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

Consider the call flow initiated by the following client request:

```csharp
var slowpoke = client.GetGrain<ISlowpokeGrain>(0);

// A. This will take around 20 seconds.
await Task.WhenAll(slowpoke.GoSlow(), slowpoke.GoSlow());

// B. This will take around 10 seconds.
await Task.WhenAll(slowpoke.GoFast(), slowpoke.GoFast(), slowpoke.GoFast());
```

Calls to `GoSlow` aren't interleaved, so the total execution time of the two `GoSlow` calls is around 20 seconds. On the other hand, `GoFast` is marked <xref:Orleans.Concurrency.AlwaysInterleaveAttribute>. The three calls to it execute concurrently, completing in approximately 10 seconds total instead of requiring at least 30 seconds.

### Readonly methods

When a grain method doesn't modify the grain state, it's safe to execute concurrently with other requests. The <xref:Orleans.Concurrency.ReadOnlyAttribute> indicates that a method doesn't modify the grain's state. Marking methods as <xref:Orleans.Concurrency.ReadOnlyAttribute> allows Orleans to process your request concurrently with other <xref:Orleans.Concurrency.ReadOnlyAttribute> requests, which can significantly improve your app's performance. Consider the following example:

```csharp
public interface IMyGrain : IGrainWithIntegerKey
{
    Task<int> IncrementCount(int incrementBy);

    [ReadOnly]
    Task<int> GetCount();
}
```

The `GetCount` method doesn't modify the grain state, so it's marked <xref:Orleans.Concurrency.ReadOnlyAttribute>. Callers awaiting this method invocation aren't blocked by other <xref:Orleans.Concurrency.ReadOnlyAttribute> requests to the grain, and the method returns immediately.

### Call chain reentrancy

If a grain calls a method on another grain, which then calls back into the original grain, the call results in a deadlock unless the call is reentrant. You can enable reentrancy on a per-call-site basis using *call chain reentrancy*. To enable call chain reentrancy, call the <xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy> method. This method returns a value allowing reentrance from any caller further down the call chain until the value is disposed. This includes reentrance from the grain calling the method itself. Consider the following example:

```csharp
public interface IChatRoomGrain : IGrainWithStringKey
{
    ValueTask OnJoinRoom(IUserGrain user);
}

public interface IUserGrain : IGrainWithStringKey
{
    ValueTask JoinRoom(string roomName);
    ValueTask<string> GetDisplayName();
}

public class ChatRoomGrain : Grain<List<(string DisplayName, IUserGrain User)>>, IChatRoomGrain
{
    public async ValueTask OnJoinRoom(IUserGrain user)
    {
        var displayName = await user.GetDisplayName();
        State.Add((displayName, user));
        await WriteStateAsync();
    }
}

public class UserGrain : Grain, IUserGrain
{
    public ValueTask<string> GetDisplayName() => new(this.GetPrimaryKeyString());
    public async ValueTask JoinRoom(string roomName)
    {
        // This prevents the call below from triggering a deadlock.
        using var scope = RequestContext.AllowCallChainReentrancy();
        var roomGrain = GrainFactory.GetGrain<IChatRoomGrain>(roomName);
        await roomGrain.OnJoinRoom(this.AsReference<IUserGrain>());
    }
}
```

In the preceding example, `UserGrain.JoinRoom(roomName)` calls `ChatRoomGrain.OnJoinRoom(user)`, which tries to call back into `UserGrain.GetDisplayName()` to get the user's display name. Since this call chain involves a cycle, it results in a deadlock if `UserGrain` doesn't allow reentrance using one of the supported mechanisms discussed in this article. In this instance, we use <xref:Orleans.Runtime.RequestContext.AllowCallChainReentrancy>, which allows only `roomGrain` to call back into `UserGrain`. This gives you fine-grained control over where and how reentrancy is enabled.

If you were to prevent the deadlock by annotating the `GetDisplayName()` method declaration on `IUserGrain` with <xref:Orleans.Concurrency.AlwaysInterleaveAttribute> instead, you would allow *any* grain to interleave a `GetDisplayName` call with any other method. By using `AllowCallChainReentrancy`, you allow *only* `roomGrain` to call methods on the `UserGrain`, and only until `scope` is disposed.

#### Suppress call chain reentrancy

You can also *suppress* call chain reentrance using the <xref:Orleans.Runtime.RequestContext.SuppressCallChainReentrancy> method. This has limited usefulness for end developers but is important for internal use by libraries extending Orleans grain functionality, such as [streaming](../streaming/index.md) and [broadcast channels](../streaming/broadcast-channel.md), to ensure developers retain full control over when call chain reentrancy is enabled.

### Reentrancy using a predicate

Grain classes can specify a predicate to determine interleaving on a call-by-call basis by inspecting the request. The `[MayInterleave(string methodName)]` attribute provides this functionality. The argument to the attribute is the name of a static method within the grain class. This method accepts an <xref:Orleans.CodeGeneration.InvokeMethodRequest> object and returns a `bool` indicating whether the request should be interleaved.

Here's an example that allows interleaving if the request argument type has the `[Interleave]` attribute:

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class InterleaveAttribute : Attribute { }

// Specify the may-interleave predicate.
[MayInterleave(nameof(ArgHasInterleaveAttribute))]
public class MyGrain : Grain, IMyGrain
{
    public static bool ArgHasInterleaveAttribute(IInvokable req)
    {
        // Returning true indicates that this call should be interleaved with other calls.
        // Returning false indicates the opposite.
        return req.Arguments.Length == 1
            && req.Arguments[0]?.GetType()
                    .GetCustomAttribute<InterleaveAttribute>() != null;
    }

    public Task Process(object payload)
    {
        // Process the object.
    }
}
```
