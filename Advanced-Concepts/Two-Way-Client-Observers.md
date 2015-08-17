---
layout: page
title: Two Way Client Observers
---
{% include JB/setup %}

In addition to a regular, one way, [Client Observers](http://dotnet.github.io/orleans/Getting-Started-With-Orleans/Observers), 
Orleans also provides an advanced feature of two way (RPC) client observers. 
This feature allows a grain to make a call into a client observer and receive a response back.

## Programming Interface

To enable this feature you start with writing a regular client observer - a class that extends `IGrainObserver` interface.
To make it two way you need to add the following:
1) Add a `[Factory(FactoryAttribute.FactoryTypes.ClientObject)]`  attribute on this class.
2) Change the methadone signatures to be async (return `Task` or `Task<T>`).

From now on follow the regular observers programming pattern: call `CreateObjectReference()` to create an observer reference and then send it to a grain that will use it to notify the client.
The notifying grain can use this observer reference as any regular grain reference: make calls on it and `await` them. Notice that since the observer reference represents a client object 
and not a virtual actor (grain), if the actual client is down, or the observer reference on the client was deleted 
(either by an explicit call to `DeleteObjectReference` or just garbage collected since it was not rooted), the `Task` returned from the call to it will break with an exception.

## Usage guidelines

Please take into account that making RPC calls from actors (a server) to clients may sometimes be considered an **anti-pattern**.
Traditional distributed system client server application usually do not allow such capability. The reason is that it can potentially create **too strong coupling 
between the client and the server**, making: (a) server logic relay too much on the client logic, (b) making server resources consumption be directly impacted and controlled by the client.
In traditional synchronous RPC systems making an RPC call from a server to client would mean that the server thread is blocked until the client responds, potentially taking valuable resources.
In the general case of uncontrolled (and maybe even malicious) clients, this is a realy bad pattern. Therefore, traditional distributed system avoided such capability.

In Orleans context we feel this feature is less potentiality dangerous since:

1) In Orleans clients are usually considered part of the trusted domain, running usually as front ends in the same service deployment. Therefore, they are assumed to be non malicious.

2) Orleans RPCs are asynchronous and essentially do not consume any additional resources beyond a regular message call 
(we still need to store a callback context to be able to process the response, just like we do for regular grain to grain calls, but this is a small object and its overhead is very limited).

One should still consider the potential dangers of using two way client RPCs with Orleans:

If the grain making the call is non re-entrant (the default mode) the grain will be blocked until the response arrives or the built-in time-out occurs. 
It means the grain might be unresponsive for some time. Even with non malicious clients, since Orleans does not control client side resources, 
it cannot guarantee that client responds in a timely manner to this RPC call. 
As such, even in Orleans, this feature creates a tighter coupling between the client and the server, which is generally undesired.

Therefore, our recommendation is to use this feature sparsely, only if you fully control and trust the client code, and if all other alternative solutions 
(regular [one way client side observers[(http://dotnet.github.io/orleans/Getting-Started-With-Orleans/Observers)) and (client streams](http://dotnet.github.io/orleans/Orleans-Streams/)) do not work for your scenario.





