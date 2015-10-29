---
layout: page
title: Interceptors
---
{% include JB/setup %}

Orleans provides a way to intercept grain invocation calls and inject an arbitrary application logic into the invocation path. We currectly support only client side pre-call inteceptors. 

## Client side interceptors

If client side interceptor is defined, any grain call made from Orleans client will invoke this interceptor before the call is dispatched remotely. The interceptor is invoked synchronously in the same thread where the call is made after call arguments were deep copied. Since the interceptor is invoked synchronously it should return promptly and do a minimum work, to avoid blocking calling thread or impacting throughput. The interceptor is allowed to mutate the call arguments and also mutate the [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context). Any changes made by the interceptor to `Orleans.RequestContext` will be picked up as part of the call dispatch logic that occurs after the interceptor. If the interceptor logic throws an exception, the remote call will not be made and the client calling code will throw promptly.

The interceptor can be set by setting `GrainClient.ClientInvokeCallback`, which is a property of type `Action<InvokeMethodRequest, IGrain>`. The first argument is the invocation request that includes varios details about the invoked call, such as `InterfaceId` and `MethodId`, as well as deep-copied arguments. The second argument is the target grain reference to which this call is made.

Currently, the main scenario that we know of that uses client side pre-call inteceptors is to add some extra information to [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context), such as any special call context or token.

## Server side interceptors

If a server-side interceptor is defined, Orleans will invoke this interceptor on the silo before the grain method is invoked locally. The interceptor is invoked synchronously in the same thread where the call is made. Since the interceptor is invoked synchronously it should return promptly and do a minimum work, to avoid blocking calling thread or impacting throughput. The interceptor is allowed to mutate the call arguments and also mutate the [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context). If the interceptor logic throws an exception, the grain method will not be invoked and the exception will be propagated back to the client.

The interceptor can be set by setting `IProviderRuntime.PreInvokeCallback`, usually from within a [Bootstrap Provider](https://dotnet.github.io/orleans/Advanced-Concepts/Application-Bootstrap-within-a-Silo). `IProviderRuntime.PreInvokeCallback` is a property of type `Action<InvokeMethodRequest, IGrain>`. The first argument is the invocation request that includes varios details about the invoked call, such as `InterfaceId` and `MethodId`, as well as deep-copied arguments. The second argument is the grain activation which will serve the request.


