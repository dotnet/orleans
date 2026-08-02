---
title: Orleans observers
description: Send asynchronous notifications from grains to clients or other grains.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans observers

Observers let grains call an object hosted by an Orleans client or another grain. They are useful for live, best-effort notifications while the receiver is connected.

Observers aren't durable subscriptions. A client can disconnect without notice, and a recreated observer has a different identity. Use an Orleans stream or another durable messaging mechanism when subscriptions or delivery must survive failures.

## Define an observer

Observer interfaces derive from <xref:Orleans.IGrainObserver>:

```csharp
public interface IChatObserver : IGrainObserver
{
    Task ReceiveMessage(string room, string message);
}

public sealed class ChatObserver : IChatObserver
{
    public Task ReceiveMessage(string room, string message)
    {
        Console.WriteLine($"[{room}] {message}");
        return Task.CompletedTask;
    }
}
```

Use asynchronous return types. Avoid `async void`. Apply <xref:Orleans.Concurrency.OneWayAttribute> only when notifications are deliberately best effort and the publisher doesn't need exceptions or completion.

## Create and remove a client observer reference

Convert the local object into an addressable reference:

```csharp
var observer = new ChatObserver();
IChatObserver observerReference =
    grainFactory.CreateObjectReference<IChatObserver>(observer);

IChatRoomGrain room =
    grainFactory.GetGrain<IChatRoomGrain>("general");

await room.Subscribe(observerReference);
```

Keep a strong reference to the local observer for as long as it should receive calls. When finished, unsubscribe and delete the object reference:

```csharp
await room.Unsubscribe(observerReference);
grainFactory.DeleteObjectReference<IChatObserver>(observerReference);
```

Deleting the reference releases the client-side registration. Failing to delete long-lived registrations can leak resources.

## Manage subscriptions in a grain

<xref:Orleans.Utilities.ObserverManager`1> tracks observers, expires stale entries, and removes observers whose notifications fail:

```csharp
public sealed class ChatRoomGrain : Grain, IChatRoomGrain
{
    private readonly ObserverManager<IChatObserver> _observers;

    public ChatRoomGrain(ILogger<ChatRoomGrain> logger)
    {
        _observers = new(
            TimeSpan.FromMinutes(5),
            logger);
    }

    public Task Subscribe(IChatObserver observer)
    {
        _observers.Subscribe(observer, observer);
        return Task.CompletedTask;
    }

    public Task Unsubscribe(IChatObserver observer)
    {
        _observers.Unsubscribe(observer);
        return Task.CompletedTask;
    }

    public Task Publish(string message)
    {
        return _observers.Notify(
            observer => observer.ReceiveMessage(
                this.GetPrimaryKeyString(),
                message));
    }
}
```

The current API is `Notify`, including the overload that accepts `Func<TObserver, Task>`. There is no `NotifyAsync` method.

Subscriptions expire lazily after `ExpirationDuration`. Clients should renew before expiry. A notification exception causes `ObserverManager` to remove that observer; it doesn't fail the entire publish operation.

Use <xref:Orleans.Utilities.ObserverManager`2> when the subscription identity should differ from the observer reference.

## Grain observers

A grain can implement an observer interface and pass a reference to itself:

```csharp
IChatObserver observer =
    this.AsReference<IChatObserver>();

await room.Subscribe(observer);
```

Don't call `CreateObjectReference` for a grain. Grains are already addressable.

## Execution and cancellation

Calls to one client observer reference execute sequentially and aren't reentrant. Different observer references can execute concurrently.

Observer methods can accept a <xref:System.Threading.CancellationToken> parameter. Cancellation remains cooperative and doesn't make observer delivery durable.
