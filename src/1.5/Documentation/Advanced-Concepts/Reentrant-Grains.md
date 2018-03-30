---
layout: page
title: Reentrant Grains
---

[!include[](../../warning-banner.md)]

# Reentrant Grains

By default, the Orleans scheduler requires an activation to completely finish processing one request before invoking the next request.
An activation cannot receive a new request until all of the `Task`s created (directly or indirectly) in the processing of the current request have been resolved and all of their associated closures executed.
`Grain` implementation classes may be marked with the `[Reentrant]` attribute to indicate that turns belonging to different requests may be freely interleaved.

In other words, a reentrant activation may start executing another request while a previous request has not finished processing and has pending closures.
Execution of turns of both requests are still limited to a single thread.
So the activation is still executing one turn at a time, and each turn is executing on behalf of only one of the activation’s requests.

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

Reentrant grains should have slightly less overhead because of fewer activations, scheduling queues, smaller directory, and resources proportional to the number of activations. How small or large the “slightly” depends on what order of numbers we are talking about here, and on the potential overhead of handling interleaving requests (extra copies of state, etc.).

The main tradeoff in choosing between reentrant and non-reentrant grains is the code complexity to make interleaving work correctly and the difficulty to reason about it.

In a trivial case when the grains are stateless and the logic is simple, fewer (but not too few, so that all the hardware threads are used) reentrant grains should be in general slightly more efficient.

If the code is more complex, then a larger number of non-reentrant grains, even if slightly less efficient overall, should save you a lot of grief of figuring out non-obvious interleaving issues.

In the end answer will depend on the specifics of the application.
