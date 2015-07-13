---
layout: page
title: Reentrant Grains
---
{% include JB/setup %}

Grains can be marked [Reentrant] which allows the Orleans runtime to perform some optimizations with respect to interleaving processing of different requests.

Reentrant grain code will never run multiple pieces of grain code in parallel (execution of grain code will always be single-threaded), but reentrant grains **may **see the execution of code for different requests interleaving. That is, the continuation turns from different requests may interleave.

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

If this grain is marked [Reentrant], the execution of Foo and Bar may interleave. 

For example, the following order of execution is possible:

Line 1, line 3, line 2 and line 4. That is, the turns from different requests interleave.

If the grain was not reentrant, the only possible executions would be: line 1, line 2, line 3, line 4 OR: line 3, line 4, line 1, line 2 (new request cannot start before the previous one finished).

Reentrant grains should have slightly less overhead because of fewer activations, scheduling queues, smaller directory, and resources proportional to the number of activations. How small or large the “slightly” depends on what order of numbers we are talking about here, and on the potential overhead of handling interleaving requests (extra copies of state, etc.). 

The main tradeoff in choosing between reentrant and non-reentrant grains is the code complexity to make interleaving work correctly and the difficulty to reason about it. 

In a trivial case when the grains are stateless and the logic is simple, fewer (but not too few, so that all the hardware threads are used) reentrant grains should be in general slightly more efficient. 

If the code is more complex, then a larger number of non-reentrant grains, even if slightly less efficient overall, should save you a lot of grief of figuring out non-obvious interleaving issues. 

In the end answer will depend on the specifics of the application.