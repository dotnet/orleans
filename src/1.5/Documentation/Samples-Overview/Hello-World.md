---
layout: page
title: Hello World
---

[!include[](../../warning-banner.md)]

# Hello World

In this sample, a client connects with an Orleans grain instance, sends it a greeting and receives a greeting back. The client then prints that greeting and that's that. Simple enough in theory, but since there's distribution involved, there's a bit more to it.

There are three projects involved -- one for declaring the communication interfaces, one for the grain implementations, and one for the client, which also hosts the Orleans silo that loads the grain when activated.

There's only one communication interface, in IHello.cs:

``` csharp
public interface IHello : Orleans.IGrainWithIntegerKey
{
   Task<string> SayHello(string greeting);
}
```

This is simple enough, and we can see that all replies must be represented as a Task or Task<T> in communication interfaces. The implementation, found in HelloGrain.cs, is similarly trivial:

``` csharp
public class HelloGrain : Orleans.Grain, HelloWorldInterfaces.IHello
{
    Task<string> HelloWorldInterfaces.IHello.SayHello(string greeting)
    {
        return Task.FromResult("You said: '" + greeting + "', I say: Hello!");
    }
}
```

The class inherits from an Orleans-defined base class, and implements the communication interface defined earlier. Since there is nothing that the grain needs to wait on, the method is not declared `async` and instead returns its value using `Task.FromResult()`.

 The client, which orchestrates the grain code and is found in Program.cs, looks like this:

``` csharp
Orleans.GrainClient.Initialize("DevTestClientConfiguration.xml");
var friend = GrainClient.GrainFactory.GetGrain<IHello>(0);
Console.WriteLine("\n\n{0}\n\n", friend.SayHello("Good morning!").Result);
```


There's other code in the method, too, but that is unrelated to the client logic, it's hosting the Orleans silo.
