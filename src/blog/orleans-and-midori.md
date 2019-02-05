Orleans and Midori
==================

[Sergey Bykov](https://github.com/sergeybykov)
12/11/2016 10:56:13 PM

* * * * *

Reading the epic Joe Duffy’s [15 Years of Concurrency](http://joeduffyblog.com/2016/11/30/15-years-of-concurrency/) post brought some old memories from the early days of Orleans.
It even compelled me to dig up and try to compile the code from 2009.
It was an entertaining exercise.

When we were just starting the Orleans project, we would meet and talk with Midori people on a regular basis.
That was natural not only because of some obvious overlap of the problem spaces, but also because Jim Larus who conceived Orleans was one of the creators of [Singularity](https://www.microsoft.com/en-us/research/project/singularity/), the base from which Midori started.
We immediately borrowed the promises library of Midori because we wanted to use the promise-based concurrency
for safe execution and efficient RPC. We didn’t bother to try to
integrate the code, and simply grabbed the binaries and checked them in
into our source tree. We were at an early prototyping stage, and didn’t
have to worry about the long term yet.

At the time, grain interfaces looked like this:

```csharp
[Eventual]
public interface ISimpleGrain : IEventual
{
    [Eventual]
    PVoid SetA(int a);

    [Eventual]
    PVoid SetB(int b);

    [Eventual]
    PInt32 GetAxB();
}
```

`PVoid` and `Pint32` were moral equivalents of [Task and Task\<int\> in TPL](https://msdn.microsoft.com/en-us/library/dd537609(v=vs.110).aspx).
Unlike Tasks, they had a bunch of static methods, with one of the simpler overloads taking two lambdas: one for success case and one to
handle a thrown exception:  

```csharp
public static PVoid When(PVoid target, Action fn, Action<Exception> catchFn);
```

A trivial grain method looked like:

```csharp
public PVoid SetA(int a)
{
    this.a = a;
    return PVoid.DONE;
}
```

You can see here where TaskDone.Done came from.
A simple unit test method looked rather convoluted:

```csharp
[TestMethod]
public void SimpleGrainDataFlow()
{
    result = new ResultHandle();
    Runner.Enqueue(new SimpleTodo(() =>
    {

       Promise<SimpleGrainReference> clientPromise = SimpleGrainReference.GetReference("foo");
       PVoid.When(clientPromise,
           reference =>
           {
                grain = reference;
                Assert.IsNotNull(grain);
                PVoid setPromise = grain.SetA(3);
                PVoid.When(setPromise,
                    () =>
                    {
                        setPromise = grain.SetB(4);
                        PVoid.When(setPromise,
                            () =>
                            {
                                PInt32 intPromise = grain.GetAxB();
                                PVoid.When<Int32>(intPromise,
                                    x =>
                                    {
                                        result.Result = x;
                                        result.Done = true;
                                    },
                                    exc =>
                                    {
                                        Assert.Fail("Exception thrown by GetAxB: " + exc.Message);
                                        return PVoid.DONE;
                                    });
                            },
                            exc =>
                            {
                                Assert.Fail("Exception thrown by SetB: " + exc.Message);
                                return PVoid.DONE;
                            });
                    },
                    exc =>
                    {
                        Assert.Fail("Exception thrown by SetA: " + exc.Message);
                        return PVoid.DONE;
                    });
            },
            exc =>
            {
                result.Exception =  exc;
                result.Done = true;
                return PVoid.DONE;
            });
    })); 

    Assert.IsTrue(result.WaitForFinished(timeout));
    Assert.IsNotNull(result.Result);
    Assert.AreEqual(12, result.Result);
}
```
 

The nested Whens were necessary to organize a data flow execution pipeline.
`Runner` was an instance of `ForeignTodoRunner`, which was one of the ways of injecting asynchronous tasks (`ToDo`s) into a `TodoManager`.
`TodoManager` was a single-threaded execution manager a.k.a. a vat, [the notion that came from E language](http://www.erights.org/elib/concurrency/vat.html).
Initialization of the vat-based execution system was a few lines of code:

 
```csharp
todoManager = new TodoManager();

Thread t = new Thread(todoManager.Run);
t.Name = "Unit test TodoManager";
t.Start();

runner = new ForeignTodoRunner(todoManager);
```
 

Within a silo, we also used vats for managing single-threaded execution of grain turns.
As part of silo startup we set up N of them to match the number of available CPU cores:

```csharp
for (int i = 0; i < nTodoManagers; i++)
{
    todoManagers[i] = new TodoManager();

    for (int j = 0; j < runnerFactor; j++)
        todoRunners[i \* runnerFactor + j] = new ForeignTodoRunner(todoManagers[i]);

    Thread t = new Thread(todoManagers[i].Run);
    t.Name = String.Format("TodoManager: {0}", i);
    t.Start();
}
```

We argued with Dean Tribble at the time that using static methods on promises in our view was too inconvenient for most developers.
We wanted them to be instance methods instead.
A few months later we introduced our own promises, AsyncCompletion and AsyncValue<T>.
They were wrappers around Task and Task<T> of TPL and had instance methods.
This compressed the code by quite a bit:

```csharp
[TestMethod]
public void SimpleGrainDataFlow()
{
    ResultHandle result = new ResultHandle(); 
    SimpleGrainReference grain = SimpleGrainReference.GetReference("foo");

    AsyncCompletion setPromise = grain.SetA(3);
    setPromise.ContinueWith(() =>
    {
        setPromise = grain.SetB(4);
        setPromise.ContinueWith(
        () =>
        {
            AsyncValue<int> intPromise = grain.GetAxB();
            intPromise.ContinueWith(
            x =>
            {
                result.Result = x;
                result.Done = true;
            });
        });
    });

    Assert.IsTrue(result.WaitForFinished(timeout));
    Assert.IsNotNull(result.Result);
    Assert.AreEqual(12, result.Result);
}
```

Initially, we allowed grain methods to be synchronous, and had grain references be their asynchronous proxies.

```csharp
public class SimpleGrain : GrainBase
{
    public void SetA(int a)

    public void SetB(int b)

    public int GetAxB()
}

public class SimpleGrainReference : GrainReference
{
    public AsyncCompletion SetA(int a)

    public AsyncCompletion SetB(int b)

    public AsyncValue<int> GetAxB()
}
```

We quickly realized that was a bad idea, and switched to grain methods returning `AsyncCompletion`/`AsyncValue<T>`.
We went through and eventually discarded a number of other bad ideas.
We supported properties on grain classes.
Async setters were a problem, and in general, async properties were rather misleading and provided no benefit over explicit getter methods.
We initially supported .NET events on grains.
Had to scrap them because of the fundamentally synchronous nature of += and -= operations in .NET.

Why didn’t we simply use `Task`/`Task<T>` instead of `AsyncCompletion`/`AsyncValue<T>`?

We needed to intercept every scheduling and continuation call in order to guarantee single-threaded execution.
`Task` was a sealed class, and hence we couldn’t subclass it to override the key methods we needed.
We didn’t have a custom TPL scheduler yet either.

After we switched to using our own promises, we lost the opportunity to use some of the advanced features that Midori had for theirs.
For example, they supported a three-party promise handoff protocol.
If node A called node B and held a promise for that call, but B as part of processing the request made a call to C for the final value, B could hand off a reference to the promise held by A, so that C could reply directly to A instead of making an extra hop back to B.
In this tradeoff between performance and complexity we chose to prioritize for simplicity.

Another lesson we learned from talking to Midori people was that the source of some of the hardest to track down bugs in their codebase was interleaving of
execution turns.
Even though a vat had a single thread to execute all turns (synchronous pieces of code between yield points), it was totally legal for it to execute turns belonging to different requests in an arbitrary order.

Imagine your component is processing a request and needs to call another component, for example, make an IO call in the middle of it.
You make that IO call, receive a promise for its completion or its return value, and schedule a continuation with a `When` or `ContinueWith` call.
The trap here is that when the IO call completes and the scheduled continuation starts executing, it is too easy to assume that the state of the component hasn’t changed since the IO call was issued.
In fact, the component might have received and processed a number of other requests while asynchronously waiting for that IO call, and processing of those requests could have mutated the state of the component in a non-obvious way.
The Midori team was very senior. At the time, the majority of them were principal and partner level engineers and architects.
We wondered if interleaving was so perilous to people of that caliber and experience, it must be even worse for mere mortals like us.
That lead to the later decision to make grains in Orleans non-reentrant by default.

At around the same time, Niklas Gustafsson worked on project [Maestro](https://channel9.msdn.com/shows/Going+Deep/Maestro-A-Managed-Domain-Specific-Language-For-Concurrent-Programming/) that was later renamed and released as [Axum](https://web.archive.org/web/20090511155406/http:/msdn.microsoft.com/en-us/devlabs/dd795202.aspx).
We had an intern prototype one of the early Orleans applications on Axum to compare the programming experience with the promise-based one in spring of 2009.
We concluded that the promises model was more attainable for developers.
In parallel Niklas created a proposal and a prototype of what eventually, after he convinced Anders Hejlsberg and others, became the `async`/`await` keywords in C\#.
By now it propagated to even more languages.

After .NET 4.5 with async and await was released, we finally abandoned `AsyncCompletion`/`AsyncValue<T>` in favor of `Task`/`Task<T>` to leverage the power of await.
It was another tradeoff that made us rewrite our scheduler a couple of times (not a trivial task) and give up some of the nice features we had in our promises.
For example, before we could easily detect if grain code tried to block the thread by calling `Result` or `Wait()` on an unresolved promise, and throw an
`InvalidOperationException` to indicate that this was not allowed in the cooperative multi-tasking environment of a silo.
We couldn’t do that anymore.
But we gained the cleaner programming model that we have today:

```csharp
public interface ISimpleGrain : IGrainWithIntegerKey
{
    Task SetA(int a);

    Task SetB(int b);

    Task<int> GetAxB();
}

[Fact, TestCategory("BVT"), TestCategory("Functional")]
public async Task SimpleGrainDataFlow()
{
    var grain = GrainFactory.GetGrain<ISimpleGrain>(GetRandomGrainId());

    Task setAPromise = grain.SetA(3);
    Task setBPromise = grain.SetB(4);

    await Task.WhenAll(setAPromise, setBPromise);

    var x = await grain.GetAxB();
    Assert.Equal(12, x);
}
```

Midori was an interesting experiment of a significant scale, to try to build a ‘safe by construction’ OS with asynchrony and isolation top to bottom.
It is always difficult to judge such efforts in terms of successes, failures, and missed opportunities.
One thing is clear – Midori did influence early thinking and design about asynchrony and concurrency in Orleans, and helped bootstrap its initial prototypes.
