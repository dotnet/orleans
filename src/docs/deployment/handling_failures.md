---
layout: page
title: Handling Failures
---

# Handling Failures

> **Note:** All of the following guidance in this document is provided to serve as examples and food for thought.
> You should not think of them as prescriptive solutions to your problems because failure handling is a rather application-specific subject.
> These patterns and others are only useful if applied with a good knowledge of the concrete case being worked on.

The hardest thing in programming a distributed system is handling failures.
The actor model and the way it works makes it much easier to deal with different kinds of failures, but as a developer, you are responsible for dealing with the failure possibilities and handling them in an appropriate way.

## Types of failures

When you are coding your grains, all calls are asynchronous and have the potential to go over the network.
Each grain call can possibly fail due to one of the following reasons.

- The grain was activated on a silo which is unavailable at the moment due to a network partition crash or some other reason. If the silo has not been declared dead yet, your request might time out.
- The grain method call can throw an exception signaling that it failed and can not continue its job.
- An activation of the grain doesn't exist and cannot be created because the `OnActivateAsync` method throws an exception or is dead-locked.
- Network failures don't let you to communicate with the grain before timeout.
- Other potential reasons

## Detection of failures

Getting a reference to a grain always succeeds and is a local operation.
However, method calls can fail, and when they do, you get an exception.
You can catch the exception at any level you need and they are propagated even across silos.

## Recovering from failures

Part of the recovery job is automatic in Orleans and if a grain is not accessible anymore, Orleans will reactivate it in the next method call.
The thing you need to handle and make sure is correct in the context of your application is the state.
A grain's state can be partially updated or the operation might be something which should be done across multiple grains and is carried on partially.

After you see a grain operation fail you can do one or more of the following.

- Simply retry your action, especially if it doesn't involve any state changes which might be half done.
This is by far the most typical case.
- Try to fix/reset the partially changed state by calling a method which resets the state to the last known correct state or just reads it from storage by calling `ReadStateAsync`.
- Reset the state of all related activations as well to ensure a clean state for all of them.
- Perform multi-grain state manipulations using a [Process Manager](https://msdn.microsoft.com/en-us/library/jj591569.aspx) or database transaction to make sure it's either done completely or nothing is changed to avoid the state being partially updated.

Depending on your application, the retry logic might follow a simple or complex pattern, and you might have to do other stuff like notifying external systems and other things, but generally you either have to retry your action, restart the grain/grains involved, or stop responding until something which is not available becomes available.

If you have a grain responsible for database saves and the database is not available, you simply have to fail any request until the database comes back online.
If your operation can be retried at the user's will, like failure of saving a comment in a comment grain, you can retry when the user presses the retry button (until a certain number of times in order to not saturate the network).
Specific details of how to do it are application specific, but the possible strategies are the same.

## Strategy parameters and choosing a good strategy

As described in the section above, the strategy you choose depends on the application and context.
Strategies usually have parameters which have to be decided at the application level.
For example, if no other processes depend on an action, then you might decide to retry that action a maximum of 5 times per minute until it eventually completes. But you would have to process a user's Login grain request before processing any other requests from that user.
If the login action is not working, then you cannot continue.

There is a guide [in the Azure documentation](https://docs.microsoft.com/en-us/azure/architecture/patterns/) about good patterns and practices for the cloud which applies to Orleans as well, in most cases.

## A fairly complex example

Because in Orleans grains are activated and deactivated automatically and you don't handle their life-cycle, you usually only deal with making sure that grain state is correct and actions are being started and finished correctly in relation to each other.
Knowing the dependencies between grains and actions they perform is a big step toward understanding how to handle failure in any complex system. 
If you need to store relations among grains, you can simply do it and it is a widely followed practice, too.

As an example, let's say we have a `GameManager` grain which starts and stops `Game` grains and adds `Player` grains to the games.
If my `GameManager`grain fails to do its action regarding starting a game, the related game belonging to it should fail to do its `Start()` as well and the manager can do this for the game by doing orchestration.
Managing memory in Orleans is automatic and the system deals with it, you only need to make sure that the game starts and only if manager can do its `Start()` as well.
You can achieve this by either calling the related methods in a sequential manner or by doing them in parallel and resetting the state of all involved grains if any of them fail.

If one of the games receives a call, it will be reactivated automatically, so if you need the manager to manage the game grains, then all calls to the game which are related to management should go through the `GameManager`.
If you need orchestration among grains, Orleans doesn't solve it "automagically" for you and you need to do your orchestration. 
But the fact that you are not dealing with creating/destroying grains means you don't need to worry about resource management.
You don't need to answer any of these questions:

- Where should I create my supervision tree?
- which grains should I register to be addressable by name?
- Is grain X alive so I can send it a message?
- ...

So, the game start example can be summarized like this:

- `GameManager` asks the `Game` grain to start
- `Game` grain adds the `Player` grains to itself
- `Game` asks `Player` grains to add game to themselves
- `Game` sets its state to be started.
- `GameManager` adds the game to its list of games.

Now, if a player fails to add the game to itself, you don't need to kill all players and the game and start over.
You simply reset the state of the other players which added the game to themselves, reset the state of the `Game` and `GameManager` (if required), and redo your work or declare a failure.
If you can deal with adding the game to the player later on, you can continue and retry doing that again in a reminder or at some other game call like the `Finish()` method of the game.
