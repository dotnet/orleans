---
layout: page
title: Interceptors
---

# Interceptors

Orleans provides a way to intercept grain invocation calls and inject an arbitrary application logic into the invocation path.

## Client side interceptors

If a client side interceptor is defined, any grain call made from an Orleans client will invoke this interceptor before the call is dispatched remotely. The interceptor is invoked synchronously in the same thread where the call is made after call arguments are deep copied. Since the interceptor is invoked synchronously it should return promptly and do minimal work, to avoid blocking the calling thread or impacting throughput. The interceptor is allowed to mutate the call arguments and also mutate the [`Orleans.RequestContext`](http://dotnet.github.io/orleans/Advanced-Concepts/Request-Context). Any changes made by the interceptor to `Orleans.RequestContext` will be picked up as part of the call dispatch logic that occurs after the interceptor. If the interceptor logic throws an exception, the remote call will not be made and the client calling code will throw promptly.

The interceptor can be set by setting `GrainClient.ClientInvokeCallback`, which is a property of type `Action<InvokeMethodRequest, IGrain>`. The first argument is the invocation request that includes various details about the invoked call, such as InterfaceId and MethodId, as well as deep-copied arguments. The second argument is the target grain reference to which this call is made.

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

## Use Cases

### Exception Conversion

When an exception which has been thrown from the server is getting deserialized on the client, you may sometimes get the following exception instead of the actual one: `TypeLoadException: Could not find Whatever.dll.`

This happens if the assembly containing the exception is not available to the client. For example, say you are using Entity Framework in your grain implementations; then it is possible that an `EntityException` is thrown. The client on the other hand does not (and should not) reference `EntityFramework.dll` since it has no knowledge about the underlying data access layer.

When the client tries to deserialize the `EntityException`, it will fail due to the missing DLL; as a consequence a `TypeLoadException` is thrown hiding the original `EntityException`.

One may argue that this is pretty okay, since the client would never handle the `EntityException`; otherwise it would have to reference `EntityFramework.dll`.

But what if the client wants at least to log the exception? The problem is that the original error message is lost. One way to workaround this issue is to intercept server-side exceptions and replace them by plain exceptions of type `Exception` if the exception type is presumably unknown on the client side.

However, there is one important thing we have to keep in mind: we only want to replace an exception **if the caller is the grain client**. We don't want to replace an exception if the caller is another grain (or the Orleans infrastructure which is making grain calls, too; e.g. on the `GrainBasedReminderTable` grain).

On the server side this can be done with a silo-level interceptor:

```csharp
private static Task RegisterInterceptors(IProviderRuntime providerRuntime, IProviderConfiguration config)
{
    var knownAssemblyNames = GetKnownExceptionTypeAssemblyNames();

    providerRuntime.SetInvokeInterceptor(async (method, request, grain, invoker) =>
    {
        if (RequestContext.Get("IsExceptionConversionEnabled") as bool? == true)
        {
            RequestContext.Remove("IsExceptionConversionEnabled");

            try
            {
                return await invoker.Invoke(grain, request);
            }
            catch (Exception exc)
            {
                var type = exc.GetType();

                if (knownAssemblyNames.Contains(type.Assembly.GetName().Name))
                {
                    throw;
                }

                // special exception handling
                throw new Exception(
                    $"Exception of non-public type '{type.FullName}' has been wrapped. Original message: <<<<----{Environment.NewLine}{exc.ToString()}{Environment.NewLine}---->>>>");
            }
        }

        return await invoker.Invoke(grain, request);
    });

    return TaskDone.Done;


private static HashSet<string> GetKnownExceptionTypeAssemblyNames()
    =>
    new HashSet<string>
    {
        typeof(string).Assembly.GetName().Name,
        "System",
        "System.ComponentModel.Composition",
        "System.ComponentModel.DataAnnotations",
        "System.Configuration",
        "System.Core",
        "System.Data",
        "System.Data.DataSetExtensions",
        "System.Net.Http",
        "System.Numerics",
        "System.Runtime.Serialization",
        "System.Security",
        "System.Xml",
        "System.Xml.Linq",

        "MyCompany.Microservices.DataTransfer",
        "MyCompany.Microservices.Interfaces",
        "MyCompany.Microservices.ServiceLayer"
};
```

On the client side you have to set up a client-side interceptor:

```csharp
GrainClient.ClientInvokeCallback = (request, grain) => { RequestContext.Set("IsExceptionConversionEnabled", true); };
```

This way the client tells the server that it wants to use exception conversion.