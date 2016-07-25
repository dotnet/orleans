---
layout: page
title: Handling Failures
---
{% include JB/setup %}

The hardest thing in programming a distributed system is handling failures. The actor model and the way it works makes it much easier to deal with different kinds of failures but still as the developer you are responsible to deal with the failure possibilities and handle them in an appropriate way.

## Types of failures

When you are coding your grains, all calls are asynchronous and potentially can go over the network. Each grain call can possibly fail due to one of the following reasons.

- The grain was activated on a silo which is unavailable at the moment due to heavy traffic, crash or some other reason. If the silo is not declared dead yet , your request might time out.
- The grain method call can throw an exception signaling that it failed and can not continue its job.
- An activation of the grain doesn't exist and can not be created because the `OnActivateAsync` method throws an exception or is dead-locked.
- Network failures don't let you to communicate with the grain before timeout.
- And potentially other reasons

## Detection of failures

Getting a reference to a grain always succeeds and is a local operation but method calls can fail and when they do, they throw an exception. You can catch the exception at any level you need and they are propagated even across silos. 

## Recovering from failures

Part of the recovery job is automatic in Orleans and if a grain is not accessible anymore, Orleans will reactivate it in the next method call. The thing you need to handle and make sure is correct in the context of your application is the state. A grain's state can be partially updated or the operation might be something which should be done across multiple grains and is carried on partially.

After you see a grain operation failed you can do one or some  of the following.

- Simply retry your action specially if it doesn't involve any state changes which might be half done.
- Try to fix/reset the partially changed state by calling a method which resets the state to the last known correct state or just reads it from storage by calling `ReadStateAsync`.
- Reset the state of all related activations as well to ensure a clean state for all of them.
- Due multi-grain state manipulations using a [Process Manager](https://msdn.microsoft.com/en-us/library/jj591569.aspx) or database transaction to make sure it's either done completely or nothing is changed to avoid the state being partially updated.

Depending on your application the retry logic might follow a simple or complex pattern and you might have to do other stuff like notifying external systems and other things but generally you either have to retry your action, restart the grain/grains involved or stop responding until something which is not available becomes available. 

If you have a grain responsible for database saves and database is not available, you simply have to fail any request until the DB comes back online. If your operation can be simply be retried at user's will like failure of saving a comment in a comment grain, you can just retry when user presses the retry button (until a certain number of times in order to not saturate the network). Specific details of how to do it are really application specific but the possible strategies are the same.

## Strategy parameters and choosing a good strategy

As described in the section above, choosing a strategy is application and context dependent and strategies usually have parameters which again have to be decided at the application level. For example you might want to retry a request maximum 5 times per minute and can deal with it being done eventually but for some other action you might not be able to continue if something is resolved. If your Login grain fails , you can not process any other requests from the user as an example. 

There is a guide [on MSDN](https://msdn.microsoft.com/en-us/library/dn568099.aspx) about good patterns and practices for the cloud which applies to Orleans in most cases as well.

## A note about supervision trees

Developers used to Erlang/Akka/Akka.Net might be surprised to see that there is no supervision tree in Orleans and wonder how they can recover from failures. The point is that in Orleans since actors are reactivated automatically and you don't handle their life-cycle, you usually only deal with making sure that actor state is correct. If you need to store relations between grains, you can simply do it and it is a widely done practice. 

As an example let's say we have a `GameManager` grain which starts and stops `Game` grains and adds `Player` grains to the games.  if my `GameManager`grain fails to do its action regarding starting a game, the related game belonging to it should fail to do its `Start()` as well and the manager can do this for the game by doing orchestration. In Erlang the point of killing an actor (process) is to clean its state and reset it and also to manage memory. Managing memory in Orleans is automatic and the system deals with it, you only need to make sure that the game starts only and only if manager can do its `Start()` as well. You can do this by either calling the related methods in a sequencial manner or doing them in parallel and reset the state of all involved grains if any of them fails.

The difference is, if one of the games receive a call, it will be reactivated automatically so if you need the manager to manage the game grains then all calls to the game which are related to management should go through the `GameManager` so be careful to honor your hierarchies just like what you do in Erlang/Akka/Akka.Net. If you need orchestration between actors, Orleans doesn't solve it automagically for you and you need to do your orchestration but the fact that you are not dealing with creating/destroying actors means you don't need to worry about resource management. You don't need to answer any of these

- Where should I create my supervision tree
- which actors should I register to be addressable by name?
- Is actor X alive so I can send it a message?
- ...

So the game start example can be summarized like this:

- `GameManager` asks the `Game` grain to start
- `Game` grain adds the `Player` grains to itself
- `Game` asks `Player` grains to add game to themselves
- `Game` sets its state to be started.
- `GameManager` adds the game to its list of games.

Now if say a player fails to add the game to itself, you don't need to kill all players and the game and start from scratch, you simply reset the state of other players which added the game to themselves and reset the state of the `Game` and `GameManager` if required and redo your work or declare failure. If you can deal with adding the game to the player later on, you can continue and retry doing that again in a reminder or at some other game call like `Finish()` method of the game.

This is faster since you are not recreating any actors and state resets can be very fast and easy if you do them in memory, even if you read it from storage you are at least saving the time which is spent on killing and recreating the actors.

All this said, It is possible to create supervisors and even other OTP concepts for Orleans if you use a unified interface and messages to communicate like how its done in [Orleankka](https://github.com/OrleansContrib/Orleankka) and also server side interseptors can be used to implement some of them even without unified interfaces. It comes to your preference of the model to use.

As an example if you want to create something like GenServer of Erlang/OTP in Orleans to manage resources, you need to define the callbacks in some way and since we can not serialize lambda expressions across silos yet, you either have to register and find the methods using reflection or use functional interfaces like those in Orleankka.

## A note on grain storage

The declarative persistence described in previous chapter is only atomic per grain and if you need to make sure storage of multiple grains are either done altogether or not at all (atomicity in storage of multiple grains) then you need to do the storage of all of them in a transaction or using a process manager as described above. Using process manager is like using a two phase commit in a database with no transaction support so if you need all ACID semantics you need to use a real transaction either implemented in grains or in a database with complete transaction support. 
## Next

Next, we'll see how we can call our grains from an MVC web application.

[Front Ends for Orleans Services](Front-Ends-for-Orleans-Services)
