---
layout: page
title: Interceptors
---
{% include JB/setup %}

Orleans provides a way to intercept grain invocation calls and inject an arbitrary application logic into the invocation path. We currectly support only client side pre-calll inteceptors. 

If client side inteceptor is defined, any grain call made from Orleans client will invoke this interceptor before the call is dispatched remotely. The interceptor is invoked synchronously in the same thread where the call is made after call arguments are deep copied. Since the interceptor is invoked synchronously it should return promptly and do a minimum work, to avoid blocking calling thread or impacting throughput. The interceptor is allowed to mutate the call arguments and also mutate the [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context). Any changes made by the interceptor to `Orleans.RequestContext` will be picked up as part of the call dispatch logic that occurs after the interceptor.

The interceptor can be set by setting `GrainClient.ClientInvokeCallback`, which is a property of type `Action<InvokeMethodRequest, IGrain>`. The first argument is the invocation request that includes varios details about the invoked call, such as InterfaceId and MethodId, as well as deep-copied arguments. The second argument is the target grain reference to which this call is made.

In the future we invision adding more interceptors, such as be client side post-calll inteceptor as well as pre and post grain side interceptors.

Currently, the main scenario that we know of that uses client side pre-call inteceptors is to add some extra information to [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context), such as any special call context or token.