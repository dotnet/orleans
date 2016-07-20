---
layout: page
title: Interceptors
---
{% include JB/setup %}

Orleans provides a way to intercept grain invocation calls and inject an arbitrary application logic into the invocation path.

## Client side interceptors

If client side interceptor is defined, any grain call made from Orleans client will invoke this interceptor before the call is dispatched remotely. The interceptor is invoked synchronously in the same thread where the call is made after call arguments were deep copied. Since the interceptor is invoked synchronously it should return promptly and do a minimum work, to avoid blocking calling thread or impacting throughput. The interceptor is allowed to mutate the call arguments and also mutate the [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context). Any changes made by the interceptor to `Orleans.RequestContext` will be picked up as part of the call dispatch logic that occurs after the interceptor. If the interceptor logic throws an exception, the remote call will not be made and the client calling code will throw promptly.

The interceptor can be set by setting `GrainClient.ClientInvokeCallback`, which is a property of type `Action<InvokeMethodRequest, IGrain>`. The first argument is the invocation request that includes varios details about the invoked call, such as InterfaceId and MethodId, as well as deep-copied arguments. The second argument is the target grain reference to which this call is made.

Currently, the main scenario that we know of that uses client side pre-call inteceptors is to add some extra information to [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context), such as any special call context or token.

## Server side interceptors

There are two methods for performing method interception on the server-side:

1. Silo-level interceptors
2. Grain-level interceptors

As their names suggest, they operate on all grain calls, and an individual grain class' calls respectively. The two methods can be used in the same silo. In that case, the silo-level interceptor will be called before the grain-level interceptor.

### Silo-level Interceptors
Silo-level interceptors are called for all grain calls within a silo. They can be installed using `IProviderRuntime.SetInvokeInterceptor(interceptor)`, typically from within a [Bootstrap Provider](https://dotnet.github.io/orleans/Advanced-Concepts/Application-Bootstrap-within-a-Silo)'s `Init` method, like so:

``` csharp
providerRuntime.SetInvokeInterceptor(async (method, request, grain, invoker) =>
{
    log.LogInfo($"{grain.GetType()}.{method.Name}(...) called");
    
    // Invoke the request and return the result back to the caller.
    var result = await invoker.Invoke(grain, request);
    log.LogInfo($"Grain method returned {result}");
    return result;
});
```

Note how the interceptor wraps the call to the grain. This allows the user to inspect the return value of each method as well as handle any exceptions which are thrown.

`SetInvokeInterceptor` takes a single parameter, a delegate of type `InvokeInterceptor` with the following signature:

``` csharp
public delegate Task<object> InvokeInterceptor(MethodInfo targetMethod, InvokeMethodRequest request, IGrain target, IGrainMethodInvoker invoker);
```

In this delegate:

* `targetMethod` is the `MethodInfo` of the method being called on the grain implementation, not the interface.
* `request.Arguments` is an `object[]` containing the arguments to the method, if any.
* `target` is the grain implementation instance being called.
* `invoker` is used to invoke the method itself.

### Grain-level interceptors

Grain-level interceptors intercept calls for individual grains only. Grain-level interceptors are enabled by implementing `IGrainInvokeInterceptor` in a grain class:

``` csharp
public interface IGrainInvokeInterceptor
{
    Task<object> Invoke(MethodInfo method, InvokeMethodRequest request, IGrainMethodInvoker invoker);
}
```

For example:

``` csharp
public Task<object> Invoke(MethodInfo methodInfo, InvokeMethodRequest request, IGrainMethodInvoker invoker)
{
    // Check access conditions.
    var isAdminMethod = methodInfo.GetCustomAttribute<AdminOnlyAttribute>();
    if (isAdminMethod && !(bool)RequestContext.Get("isAdmin"))
    {
      throw new AccessDeniedException($"Only admins can access {methodInfo.Name}!");
    }
    
    return invoker.Invoke(this, request);
}
```

If a silo-level interceptor is also present, the grain-level interceptor is invoked inside of silo-level interceptors, during the call to `invoker.Invoke(...)`. Grain-level interceptors will also be invoked for grain extensions (implementations of `IGrainExtension`), not only for method in the current class.
