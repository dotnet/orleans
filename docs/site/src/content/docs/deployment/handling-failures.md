---
title: Failure handling
description: Learn how to handle failures in Orleans apps.
ms.date: 05/23/2025
ms.topic: concept-article
---

# Failure handling

> [!TIP]
> All guidance in this document serves as examples and food for thought. Don't consider them prescriptive solutions, because failure handling is highly application-specific. These patterns and others are useful only if applied with a good understanding of the specific case.

Handling failures is often the hardest part of programming a distributed system. The actor model makes dealing with different kinds of failures much easier, but developers are responsible for considering failure possibilities and handling them appropriately.

## Types of failures

When coding grains, all calls are asynchronous and potentially go over the network. Each grain call can fail for one of the following reasons:

- The grain was activated on a silo currently unavailable due to a network partition, crash, or other reason. If the silo hasn't been declared dead yet, the request might time out.
- The grain method call can throw an exception, signaling failure and inability to continue its job.
- An activation of the grain doesn't exist and can't be created because the <xref:Orleans.Grain.OnActivateAsync*> method throws an exception or deadlocks.
- Network failures prevent communication with the grain before the timeout occurs.
- Other potential reasons.

## Detection of failures

Getting a reference to a grain always succeeds and is a local operation. However, method calls can fail. When they do, an exception is received. Catch the exception at any necessary level; Orleans propagates them even across silos.

## Recover from failures

Part of the recovery job is automatic in Orleans. If a grain is no longer accessible, Orleans reactivates it on the next method call. The part needing handling and ensuring correctness within the application's context is the state. A grain's state might be partially updated, or an operation might involve multiple grains and only complete partially.

After observing a grain operation failure, take one or more of the following actions:

- **Retry the action**: Often suitable, especially if the action doesn't involve state changes that might be left half-done. This is the most common approach.
- **Fix/reset state**: Try to correct the partially changed state. Do this by calling a method that resets the state to the last known correct state or by simply reading the correct state from storage using <xref:Orleans.Grain`1.ReadStateAsync*>.
- **Reset related states**: Reset the state of all related activations as well to ensure a consistent state across all involved grains.
- **Use transactions or process managers**: Perform multi-grain state manipulations using a Process Manager pattern or database transactions. This ensures the operation either completes entirely or leaves the state unchanged, avoiding partial updates.

Depending on the application, the retry logic might follow a simple or complex pattern. Other actions might also be needed, like notifying external systems. Generally, the options are to retry the action, restart the involved grain(s), or stop responding until a required resource becomes available again.

For example, if a grain is responsible for database saves and the database is unavailable, simply fail incoming requests until the database is back online. If an operation can be retried at the user's discretion (like failing to save a comment), allow the user to retry via a button press (perhaps limiting retries to avoid network saturation). Specific implementation details depend on the application, but the underlying strategies remain the same.

## Strategy parameters

As described above, the chosen strategy depends on the application and specific context. Strategies usually involve parameters decided at the application level. For example, if no other processes depend on an action, decide to retry that action a maximum of 5 times per minute until completion. However, successfully processing a user's Login grain request might be necessary before processing other requests from that user. If the login action fails, proceeding with other actions for that user isn't possible.

The Azure Architecture Center provides a guide on [Cloud Design Patterns](/azure/architecture/patterns), which often apply to Orleans applications as well.

## A complex example

Because Orleans activates and deactivates grains automatically, their lifecycle isn't handled directly. Instead, focus typically lies on ensuring grain state correctness and that actions involving multiple grains start and finish correctly relative to each other. Understanding dependencies between grains and their actions is crucial for handling failures in any complex system. Storing relationships between grains is certainly possible and a common practice.

As an example, consider a `GameManager` grain starting and stopping `Game` grains and adding `Player` grains to games. If the `GameManager` fails while starting a game, the corresponding `Game` grain should also fail its `Start()` operation. The manager can orchestrate this. Memory management in Orleans is automatic; just ensure the game starts successfully if, and only if, the manager also successfully completes its `Start()` related actions. Achieve this coordination by calling related methods sequentially or executing them in parallel and resetting the state of all involved grains if any step fails.

If a game grain receives a call later, Orleans reactivates it automatically. Therefore, if the manager needs to oversee game grains, all management-related calls to the game should go through the `GameManager`. If orchestration among grains is needed, Orleans doesn't solve it "automagically"; the orchestration logic must be implemented. However, because creating or destroying grains isn't handled directly, resource management isn't a concern. Questions like these don't need answering:

- Where should the supervision tree be created?
- Which grains should be registered to be addressable by name?
- Is grain X alive so a message can be sent to it?

So, the game start example can be summarized as follows:

- `GameManager` asks the `Game` grain to start.
- `Game` grain adds the `Player` grains to itself.
- `Game` asks `Player` grains to add the game to themselves.
- `Game` sets its state to started.
- `GameManager` adds the game to its list of games.

Now, if a player grain fails to add the game to itself, killing all players and the game and starting over isn't necessarily required. Simply reset the state of the other players who successfully added the game, reset the state of the `Game` and `GameManager` (if required), and either retry the operation or declare a failure. If adding the game to the player later is acceptable, continue the process and retry adding the game to that specific player using a reminder or during another game call, like the game's `Finish()` method.
