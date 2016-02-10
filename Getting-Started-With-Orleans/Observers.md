---
layout: page
title: Client Observers
---
{% include JB/setup %}

There are situations in which a simple message/response pattern is not enough, and the client needs to receive asynchronous notifications.
For example, a user might want to be notified when a new instant message has been published by a friend.

Client observers is a mechanism that allows notifying clients asynchronously.
An observer is a one-way asynchronous interface that inherits from `IGrainObserver`, and all its methods must be void.
The grain sends a notification to the observer by invoking it like a grain interface method, except that it has no return value, and so the grain need not depend on the result.
The Orleans runtime will ensure one-way delivery of the notifications.
A grain that publishes such notifications should provide an API to add or remove observers.
In addition, it is usually convenient to expose a method that allows an existing subscription to be cancelled.
Grain developers may use the Orleans `ObserverSubscriptionManager<T>` generic class to simplify development of observed grain types.

To subscribe to a notification, the client must first create a local C# object that implements the observer interface.
It then calls a static method on the observer factory, `CreateObjectReference()`, to turn the C# object into a grain reference, which can then be passed to the subscription method on the notifying grain.

This model can also be used by other grains to receive asynchronous notifications.
Unlike in the client subscription case, the subscribing grain simply implements the observer interface as a facet, and passes in a reference to itself (e.g. `this.AsReference<IMyGrainObserverInterface>`).

## Code Example

Let's assume that we have a grain that periodicaly sends messages to clients. For simplicity, the message in our example will be a  string. We first define the interface on the client taht will receive the  message.

the interface will looklike this

``` csharp
public interface IChat : IGrainObserver
{
    void ReceiveMessage(string message);
}

```

The only special thing is that the interface should inherit from `IGrainObserver`. Now any client that wants to observe those messages should implement a class which implements `IChat`.

The simplest case would be something like this:

``` csharp
public class Chat : IChat
{
    public void ReceiveMessage(string message)
    {
        Console.WriteLine(message);
    }
}
```

Now on the server we should have a Grain which sends these chat messages to clients. The Grain also should have a mechanism for clients to subscribe and unsubscribe themselves to receive notifications. For subscription the Grain can use the utility class `ObserverSubscriptionManager`:

``` csharp
class HelloGrain : Grain, IHello
{
    private ObserverSubscriptionManager<IChat> _subsManager;

    public override async Task OnActivateAsync()
    {
        // We created the utility at activation time.
        _subsManager = new ObserverSubscriptionManager<IChat>();
        await base.OnActivateAsync();
    }

    // Clients call this to subscribe.
    public async Task Subscribe(IChat observer)
    {
        _subsManager.Subscribe(observer);
    }

    //Also clients use this to unsubscribe themselves to no longer receive the messages.
    public async Task UnSubscribe(IChat observer)
    {
        _SubsManager.Unsubscribe(observer);
    }
}
```

To send the message to clients the `Notify` method of the `ObserverSubscriptionManager<IChat>` instance can be used. The method takes an `Action<T>` method or lambda expression (where `T` is of type `IChat` here). You can call any method on the interface to send it to clients. In our case we only have one method `ReceiveMessage` and our sending code on the server would loo like this:

``` csharp
public Task SendUpdateMessage(string message)
{
    _SubsManager.Notify(s => s.ReceiveMessage(message));
    return TaskDone.Done;
}

```

Now our server has a method to send messages to observer clients, two methods for subscribing/unsubscribing and the client implemented a class to be able to observe the grain messages. The last step is to create an observer reference on the client using our previously implemented `Chat` class and let it receive the messages after subscribing it.

The code would look like this:

``` csharp
//First create the grain reference
var friend = GrainClient.GrainFactory.GetGrain<IHello>(0);
Chat c = new Chat();

//Create a reference for chat usable for subscribing to the observable grain.
var obj = await GrainClient.GrainFactory.CreateObjectReference<IChat>(c);
//Subscribe the instance to receive messages.
await friend.Subscribe(obj);
```

Now whenever our grain on the server calls the `SendUpdateMessage` method, all subscribed clients will receive the message. In our client code, the `Chat` instance in variable `c` will receive the message and output it to the console.

**Note:** Support for observers might be removed in a future version and replaced with a Simple Message Stream [SMS](http://dotnet.github.io/orleans/Orleans-Streams/), which can support the same concept with more power, flexibility, and reliability.

## Next

Next we look at [Developing a Grain](Developing-a-Grain)
