---
layout: page
title: Concurrency
---

# Concurrency

Please read about [Grains](../Documentation/Getting-Started-With-Orleans/Grains.md) before following this tutorial.

Let's go back to the code that was established in the tutorial on collections of actors and modify it to demonstrate how things can go bad by creating a trivial cycle in the messaging graph: when an employee receives a greeting, he sends another greeting back to the sender and waits for the acknowledgment.
This will send a back-and-forth series of messages, until we get to 3.

First create a class in the interface project which we'll use to send the greetings around:

``` csharp
public class GreetingData
{
    public Guid From { get; set; }
    public string Message { get; set; }
    public int Count { get; set; }
}
```

`From` will be the sender of the message (the ID of the grain), `Message` will be the message text, and `Count` will be the number of times the message has been sent back and forth.
This stops us from getting a stack overflow.

We need to modify the arguments of `Greeting` on the `IEmployee` interface to :


``` csharp
Task Greeting(GreetingData data);
```

 We need to update the implementation accordingly:

``` csharp
public async Task Greeting(GreetingData data)
{
    Console.WriteLine("{0} said: {1}", data.From, data.Message);

    // stop this from repeating endlessly
    if (data.Count >= 3) return;

    // send a message back to the sender
    var fromGrain = GrainFactory.GetGrain<IEmployee>(data.From);
    await fromGrain.Greeting(new GreetingData {
        From = this.GetPrimaryKey(),
        Message = "Thanks!",
        Count = data.Count + 1 });
}
```

 We'll also update the `Manager` class, so it send the new message object:

``` csharp
public async Task AddDirectReport(IEmployee employee)
{
    _reports.Add(employee);
    await employee.SetManager(this);
    await employee.Greeting(new GreetingData {
        From = this.GetPrimaryKey(),
        Message = "Welcome to my team!" });
}
```

Now the Employee sends a message back to the manager, saying "Thanks!".

Let's add some simple client code to add a direct report to a manager:


``` csharp
var e0 = GrainClient.GrainFactory.GetGrain<IEmployee>(Guid.NewGuid());
var m1 = GrainClient.GrainFactory.GetGrain<IManager>(Guid.NewGuid());
m1.AddDirectReport(e0).Wait();
```

When we run this code, the first "Thanks!" greeting is received.
However, when this message is responded to this we get a 30 second pause (or 10 minutes when the debugger is attached), then warnings appear in the log and we're told the grain is about to break it's promise.

```
    7b66f830-8d81-49fc-b8fc-279af6924bd3 said: Welcome to my team!
    ce14310a-8500-4b2f-a21b-b4b23eb48d0d said: Thanks!    
[2014-03-12 15:25:37.398 GMT    31      WARNING 100157  CallbackData    127.0.0.1:11111]        Response did
not arrive on time in 00:00:30 for message: Request
S127.0.0.1:11111:132333898*grn/906ECA4C/00000001@68e2b3ab->S127.0.0.1:11111:132333898*grn/D9BB797F/00000000@c24c4187 #13: MyGrainInterfaces1.IEmployee:Greeting(). Target History is: <S127.0.0.1:11111:132333898:*grn/D9BB797F/00000000:@c24c4187>.
 About to break its promise.
 [2014-03-12 15:25:37.398 GMT    27      WARNING 100157  CallbackData    127.0.0.1:11111]        Response did not arrive on time in 00:00:30 for message: Request S127.0.0.1:11111:132333898*grn/D9BB797F/00000000@c24c4187->S127.0.0.1:11111:132333898*grn/D9BB797F/00000001@afc70cb4 #14: MyGrainInterfaces1.IEmployee:Greeting(). Target History is: <S127.0.0.1:11111:132333898:*grn/D9BB797F/00000001:@afc70cb4>. About to break its promise.
[2014-03-12 15:25:37.407 GMT    28      WARNING 100157  CallbackData    127.0.0.1:11111]        Response did  not arrive on time in 00:00:30 for message: Request S127.0.0.1:11111:132333898*grn/D9BB797F/00000001@afc70cb4->S127.0.0.1:11111:132333898*grn/D9BB797F/00000000@c24c4187 #15: MyGrainInterfaces1.IEmployee:Greeting(). Target History is: <S127.0.0.1:11111:132333898:*grn/D9BB797F/00000000:@c24c4187>. About to break its promise.
```

An exception is then thrown in the client code.

We've created a deadlock.
Grain 0 sends a message to grain 1.
In that call grain 1 sends a message back to grain 0.
However, grain 0 can't process it because it's awaiting the first message, so it gets queued.
The await can't complete until the second message is returned, so we've entered a state that we can't escape from.
Orleans waits for 30 seconds (10 minutes with the debugger), then kills the request.

Orleans offers us a way to deal with this, by marking the grain `[Reentrant]`, which means that additional calls may be made while the grain is waiting for a task to complete, resulting in interleaved execution.


``` csharp
[Reentrant]
public class Employee : Grain, IEmployee
{
    ...
}
```

We see that the sample works, and Orleans is able to interleave the grain calls:

 ```
 aaadb551-7dde-4dbe-82ce-1a5f2547babe said: Welcome to my team!
 63e4d07c-ac50-4012-ba50-5b5cf54e4e45 said: Thanks!
 aaadb551-7dde-4dbe-82ce-1a5f2547babe said: Thanks!
 63e4d07c-ac50-4012-ba50-5b5cf54e4e45 said: Thanks!
 ```

## Messages

Messages are simply data passed from one actor to another, we just created the `GreetingData` class to do just this.

In .NET, most objects are created from a class of some sort and are passed around by reference, something that doesn't work well with concurrency, and definitely not with distribution.

When Orleans sends a message from one grain to another, it creates a deep copy of the object, and provides the copy to the second grain, and not the object stored in the first grain.
This prohibits the mutation of state from one grain to another, one of the main tenets in the actor model is that state shouldn't be shared, and message passing is the only mechanism for exchanging data.

When the grains are in different silos, the object model is serialized to a binary format, and sent over the wire.

However, this deep copy process is expensive, and if you promise not to modify the message, then for communication with grains within a silo, it's unnecessary.

If you indicate to Orleans that you are not going to modify the object (i.e. it's immutable) then it can skip the deep copy step, and it will pass the object by reference.
There's no way Orleans or C# can stop you from modifying the state, you have to be disciplined.

Immutability is indicated with a the `[Immutable]` attribute on the class:

``` csharp
[Immutable]
public class GreetingData
{
    public Guid From { get; set; }
    public string Message { get; set; }
    public int Count { get; set; }
}
```

No other code change is required, this is just a signal to give to Orleans to tell it your not going to modify this object.

## Next

Next, we'll see how we can interact with external services from inside our grain.

[Interaction with Libraries and Services](Interaction-with-Libraries-and-Services.md)
