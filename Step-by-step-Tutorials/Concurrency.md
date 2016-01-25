---
layout: page
title: Concurrency
---
{% include JB/setup %}

Distributed applications are inherently concurrent, which leads to complexity. 
One of the things that makes the actor model special and productive is that it helps reduce some of the complexities of having to grapple with concurrency.

Actors accomplish this in two ways:

* By providing single-threaded access to the internal state of an actor instance. 
* By not sharing data between actor instances except via message-passing.

In this tutorial, we will examine both of these aspects of the programming model.

## Turn-based Execution

The idea behind the single-threaded execution model for actors is that the invokers (remote) take turns "calling" its methods.
Thus, a message coming to actor B from actor A will placed in a queue and the associated handler is invoked only when all prior messages have been serviced. 

This allows us to avoid all use of locks to protect actor state, as it is inherently protected against data races. 
However, it may also lead to problems when messages pass back and forth and the message graph forms cycles. 
If A sends a message to B from one of its methods and awaits its completion, and B sends a message to A, also awaiting its completion, the application will quickly lock up. 

To illustrate, let's go back to the code that was established in the tutorial on collections of actors and modify it to demonstrate how things can go bad by creating a trivial cycle in the messaging graph: when an employee receives a greeting, he sends another greeting back to the sender and waits for the acknowledgement. 
This will send a back-and-forth series of messages, until we get to 3. 

First create a class in the interface project which we'll use to send the greetings around:

``` csharp
public class GreetingData
{
    public long From { get; set; }
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
        From = this.GetPrimaryKeyLong(), 
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
        From = this.GetPrimaryKeyLong(),
        Message = "Welcome to my team!" });
}
```

Now the Employee sends a message back to the manager, saying "Thanks!".

Let's add some simple client code to add a direct report to a manager:


``` csharp
var e0 = GrainClient.GrainFactory.GetGrain<IEmployee>(0);
var m1 = GrainClient.GrainFactory.GetGrain<IManager>(1);
m1.AddDirectReport(e0);
```

When we run this code, the first "Thanks!" greeting is received.
However, when this message is responded to this we get a 30 second pause, then warnings appear in the log and we're told the grain is about to break it's promise.

    1 said: Welcome to my team!
    0 said: Thanks!
    [2014-03-12 15:25:37.398 GMT    31      WARNING 100157  CallbackData    127.0.0.1:11111]        Response did  
    not arrive on time in 00:00:30 for message: Request 
    S127.0.0.1:11111:132333898*grn/906ECA4C/00000001@68e2b3ab->S127.0.0.1:11111:132333898*grn/D9BB797F/00000000@c24c4187 #13: MyGrainInterfaces1.IEmployee:Greeting(). Target History is: <S127.0.0.1:11111:132333898:*grn/D9BB797F/00000000:@c24c4187>.
     About to break its promise.
     [2014-03-12 15:25:37.398 GMT    27      WARNING 100157  CallbackData    127.0.0.1:11111]        Response did not arrive on time in 00:00:30 for message: Request S127.0.0.1:11111:132333898*grn/D9BB797F/00000000@c24c4187->S127.0.0.1:11111:132333898*grn/D9BB797F/00000001@afc70cb4 #14: MyGrainInterfaces1.IEmployee:Greeting(). Target History is: <S127.0.0.1:11111:132333898:*grn/D9BB797F/00000001:@afc70cb4>. About to break its promise.
    [2014-03-12 15:25:37.407 GMT    28      WARNING 100157  CallbackData    127.0.0.1:11111]        Response did  not arrive on time in 00:00:30 for message: Request S127.0.0.1:11111:132333898*grn/D9BB797F/00000001@afc70cb4->S127.0.0.1:11111:132333898*grn/D9BB797F/00000000@c24c4187 #15: MyGrainInterfaces1.IEmployee:Greeting(). Target History is: <S127.0.0.1:11111:132333898:*grn/D9BB797F/00000000:@c24c4187>. About to break its promise.


An exception is then thrown in the client code.

We've created a deadlock. 
Grain 0 sends a message to grain 1. 
In that call grain 1 sends a message back to grain 0. 
However, grain 0 can't process it because it's awaiting the first message, so it gets queued. 
The await can't complete until the second message is returned, so we've entered a state that we can't escape from. 
Orleans waits for 30 seconds, then kills the request.

Orleans offers us a way to deal with this, by marking the grain `[Reentrant]`, which means that additional calls may be made while the grain is waiting for a task to complete, resulting in interleaved execution.


``` csharp
[Reentrant]
public class Employee : Orleans.Grain, Interfaces.IEmployee
{
    ...
}  
```

We see that the sample works, and Orleans is able to interleave the grain calls:

 ```
 1 said: Welcome to my team!
 0 said: Thanks!
 1 said: Thanks!
 0 said: Thanks!
 ```

##Messages

Messages are simply data passed from one actor to another, we just created the `GreetingData` class to do just this.

In .NET, most objects are created from a class of some sort and are passed around by reference, something that doesn't work well with concurrency, and definitely not with distribution. 

When Orleans sends a message from one grain to another, it creates a deep copy of the object, and provides the copy to the second grain, and not the object stored in the first grain. 
This prohibits the mutation of state from one grain to another, one of the main tenants in the actor model is that state shouldn't be shared, and message passing is the only mechanism for exchanging data.

When the grains are in different silos, the object model is serialized to a binary format, and sent over the wire.

However, this deep copy process is expensive, and if you promise not to modify the message, then for communication with grains within a silo, it's unnecessary.

If you indicate to Orleans that you are not going to modify the object (i.e. it's immutable) then it can skip the deep copy step, and it will pass the object by reference. 
There's no way Orleans or C# can stop you from modifying the state, you have to be disciplined.

Immutability is indicated with a the `[Immutable]` attribute on the class:

``` csharp
[Immutable]
public class GreetingData
{
    public long From { get; set; }
    public string Message { get; set; }
    public int Count { get; set; }
}
```

No other code change is required, this is just a signal to give to Orleans to tell it your not going to modify this object.

## Next

Next, we'll see how we can interact with external services from inside our grain.

[Interaction with Libraries and Services](Interaction-with-Libraries-and-Services)
