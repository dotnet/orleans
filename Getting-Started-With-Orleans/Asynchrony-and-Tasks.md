---
layout: page
title: Asynchrony and Tasks
---
{% include JB/setup %}

## Asynchrony

Grains interact by invoking asynchronous method calls. Asynchronous methods are built on top the Task Parallel Library and return either a Task (for void methods) or a Task&lt;T&gt; (for methods returning values of type T and properties of type T).

The primary way of using a Task is to wait for its completion with the await keyword of C# 5.0 (.NET 4.5).

    Guid playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}"); 
    IPlayerGrain player = PlayerGrainFactory.GetGrain(playerId) 
    await player.JoinGame(this); 
    players.Add(playerId); 
    return; 


The await keyword effectively turns the remainder of the method into a closure that will asynchronously execute upon completion of the Task being awaited without blocking the executing thread. In the above example, lines 4 and 5 will be turned into a closure by the C# compiler.

 It is also possible to join two or more Tasks; the join creates a new Task that is resolved when all of its constituent Tasks are completed. This is a useful pattern when a grain needs to start multiple computations and wait for all of them to complete before proceeding. For example, a front-end grain that generates a web page made of many parts might make multiple back-end calls, one for each part, and receive a Task for each result. The grain would then wait for the join of all of these Tasks; when the join is resolved, the individual Tasks have been completed, and all the data required to format the web page has been received.

 Example:

    List<Task> tasks = new List<Task>(); 
    ChirperMessage chirp = CreateNewChirpMessage(text); 
    foreach (IChirperSubscriber subscriber in Followers.Values) 
    { 
       tasks.Add(subscriber.NewChirpAsync(chirp)); 
    } 
    Task joinedTask = Task.WhenAll(tasks); 
    await joinedTask; 


## Turns: Units of Execution

A grain activation performs work in chunks and finishes each chunk before it moves on to the next. Chunks of work include method invocations in response to requests from other grains or external clients, and closures scheduled on completion of a Task. The basic unit of execution corresponding to a chunk of work is known as a turn.

 While Orleans may execute many turns belonging to different activations in parallel, each activation will always execute its turns one at a time. This means that there is no need to use locks or other synchronization methods to guard against data races and other multithreading hazards. As mentioned above, however, the unpredictable interleaving of turns for scheduled closures can cause the state of the grain to be different than when the closure was scheduled, so developers must still watch out for interleaving bugs.

 By default, the Orleans scheduler requires an activation to completely finish processing one request before invoking the next request. An activation cannot receive a new request until all of the Tasks created (directly or indirectly) in the processing of the current request have been resolved and all of their associated closures executed. Grain implementation classes may be marked with the  Reentrant attribute to indicate that turns belonging to different requests may be freely interleaved.

 In other words, a reentrant activation may start executing another request while a previous request has not finished processing and has pending closures. Execution of turns of both requests are still limited to a single thread. So the activation is still executing one turn at a time, and each turn is executing on behalf of only one of the activation’s requests.

## TaskDone.Done Utility Property

There is no “standard” way to conveniently return an already completed “void” Task, so Orleans sample code defines TaskDone.Done for that purposes.
