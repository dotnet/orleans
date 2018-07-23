---
layout: page
title: Interceptors
---

[!include[](../../warning-banner.md)]

# Interceptors

Grain call filters provide a means for intercepting grain calls. Filters can execute code both before and after a grain call. Multiple filters can be installed simultaneously. Filters are asynchronous and can modify [`RequestContext`](Request-Context.md), arguments, and the return value of the method being invoked. Filters can also inspect the `MethodInfo` of the method being invoked on the grain class and can be used to throw or handle exceptions.

Some example usages of grain call filters are:
* Authorization: a filter can inspect the method being invoked and the arguments or some authorization information in the [`RequestContext`](Request-Context.md) to determine whether or not to allow the call to proceed.
* Logging/Telemetry: a filter can log information and capture timing data and other statistics about method invocation.
* Error Handling: a filter can intercept exceptions thrown by a method invocation and transform it into another exception or handle the exception as it passes through the filter.

Grain call filters must implement the `IGrainCallFilter` interface, which has one method:
``` csharp
public interface IGrainCallFilter
{
    Task Invoke(IGrainCallContext context);
}
```
The `IGrainCallContext` argument passed to the `Invoke` method has the following shape:
``` csharp
public interface IGrainCallContext
{
    /// <summary>
    /// Gets the grain being invoked.
    /// </summary>
    IAddressable Grain { get; }

    /// <summary>
    /// Gets the <see cref="MethodInfo"/> of the method being invoked.
    /// </summary>
    MethodInfo Method { get; }

    /// <summary>
    /// Gets the arguments for this method invocation.
    /// </summary>
    object[] Arguments { get; }

    /// <summary>
    /// Invokes the request.
    /// </summary>
    Task Invoke();

    /// <summary>
    /// Gets or sets the result.
    /// </summary>
    object Result { get; set; }
}
```

The `IGrainCallFilter.Invoke()` method must await or return the result of `IGrainCallContext.Invoke()` to execute the next configured filter and eventually the grain method itself. The `IGrainCallContext.Result` property can be modified after awaiting the `Invoke()` method. The `IGrainCallContext.Method` property returns the `MethodInfo` of the implementation class, not the interface. The `MethodInfo` of the interface method can be accessed using reflection. Grain call filters are called for all method calls to a grain and this includes calls to grain extensions (implementations of `IGrainExtension`) which are installed in the grain. For example, grain extensions are used to implement [Streams](../Orleans-Streams/index.md) and [Cancellation Tokens](Cancellation-Tokens.md). Therefore, it should be expected that the value of `IGrainCallContext.Method` is not always a method in the grain class itself.

## Configuring Grain Call Filters

Implementations of `IGrainCallFilter` can either be registered as silo-wide filters via [Dependency Injection](../Core-Features/Dependency-Injection.md) or they can be registered as grain-level filters via a grain implementing `IGrainCallFilter` directly.

### Silo-wide Grain Call Filters

A delegate can be registered as a silo-wide grain call filters using [Dependency Injection](../Core-Features/Dependency-Injection.md) like so:
``` csharp
services.AddGrainCallFilter(async context =>
{
    // If the method being called is 'MyInterceptedMethod', then set a value
    // on the RequestContext which can then be read by other filters or the grain.
    if (string.Equals(context.Method.Name, nameof(IMyGrain.MyInterceptedMethod)))
    {
        RequestContext.Set("intercepted value", "this value was added by the filter");
    }

    await context.Invoke();

    // If the grain method returned an int, set the result to double that value.
    if (context.Result is int resultValue) context.Result = resultValue * 2;
});
```

Similarly, a class can be registered as a grain call filter using the `AddGrainCallFilter` helper method.
Here is an example of a grain call filter which logs the results of every grain method:
```csharp
public class LoggingCallFilter : IGrainCallFilter
{
    private readonly Logger log;

    public LoggingCallFilter(Factory<string, Logger> loggerFactory)
    {
        this.log = loggerFactory(nameof(LoggingCallFilter));
    }

    public async Task Invoke(IGrainCallContext context)
    {
        try
        {
            await context.Invoke();
            var msg = string.Format(
                "{0}.{1}({2}) returned value {3}",
                context.Grain.GetType(),
                context.Method.Name,
                string.Join(", ", context.Arguments),
                context.Result);
            this.log.Info(msg);
        }
        catch (Exception exception)
        {
            var msg = string.Format(
                "{0}.{1}({2}) threw an exception: {3}",
                context.Grain.GetType(),
                context.Method.Name,
                string.Join(", ", context.Arguments),
                exception);
            this.log.Info(msg);

            // If this exception is not re-thrown, it is considered to be
            // handled by this filter.
            throw;
        }
    }
}
```
This filter can then be registered using the `AddGrainCallFilter` extension method:
``` csharp
services.AddGrainCallFilter<LoggingCallFilter>();
```

Alternatively, the filter can be registered without the extension method:
``` csharp
services.AddSingleton<IGrainCallFilter, LoggingCallFilter>();
```

### Per-grain Grain Call Filters

A grain class can register itself as a grain call filter and filter any calls made to it by implementing `IGrainCallFilter` like so:
```csharp
public class MyFilteredGrain : Grain, IMyFilteredGrain, IGrainCallFilter
{
    public async Task Invoke(IGrainCallContext context)
    {
        await context.Invoke();

        // Change the result of the call from 7 to 38.
        if (string.Equals(context.Method.Name, nameof(this.GetFavoriteNumber)))
        {
            context.Result = 38;
        }
    }

    public Task<int> GetFavoriteNumber() => Task.FromResult(7);
}
```

In the above example, all calls to the `GetFavoriteNumber` method will return `38` instead of `7`, because the return value has been altered by the filter.

Another use case for filters is in access control, as in this example:
```csharp
[AttributeUsage(AttributeTargets.Method)]
public class AdminOnlyAttribute : Attribute { }

public class MyAccessControlledGrain : Grain, IMyFilteredGrain, IGrainCallFilter
{
    public Task Invoke(IGrainCallContext context)
    {
        // Check access conditions.
        var isAdminMethod = context.Method.GetCustomAttribute<AdminOnlyAttribute>();
        if (isAdminMethod && !(bool) RequestContext.Get("isAdmin"))
        {
            throw new AccessDeniedException($"Only admins can access {context.Method.Name}!");
        }

        return context.Invoke();
    }

    [AdminOnly]
    public Task<int> SpecialAdminOnlyOperation() => Task.FromResult(7);
}
```

In the above example, the `SpecialAdminOnlyOperation` method can only be called if `"isAdmin"` is set to `true` in the [`RequestContext`](Request-Context.md). In this way, grain call filters can be used for authorization. In this example, it is the responsibility of the caller to ensure that the `"isAdmin"` value is set correctly and that authentication is performed correctly. Note that the `[AdminOnly]` attribute is specified on the grain class method. This is because the `IGrainCallContext.Method` property returns the `MethodInfo` of the implementation, not the interface. The interface method can be accessed using reflection.

## Ordering of Grain Call Filters

Grain call filters follow a defined ordering:
1. `IGrainCallFilter` implementations configured in the dependency injection container, in the order in which they are registered.
2. (Obsolete) Silo-wide `InvokeInterceptor`, configured via `IProviderRuntime.SetInvokeInterceptor(...)`.
3. Grain-level filter, if the grain implements `IGrainCallFilter`.
4. (Obsolete) Grain-level interceptor, if the grain implements `IGrainInvokeInterceptor`.
5. Grain method implementation or grain extension method implementation.

Each call to `IGrainCallContext.Invoke()` encapsulates the next defined filter so that each filter has a chance to execute code before and after the next filter in the chain and eventually the grain method itself.

# Client-side interceptors

If a client side interceptor is defined, any grain call made from an Orleans client will invoke this interceptor before the call is dispatched remotely. The interceptor is invoked synchronously in the same thread where the call is made after call arguments are deep copied. Since the interceptor is invoked synchronously it should return promptly and do minimal work, to avoid blocking the calling thread or impacting throughput. The interceptor is allowed to mutate the call arguments and also mutate the [`Orleans.RequestContext`](Request-Context.md). Any changes made by the interceptor to `Orleans.RequestContext` will be picked up as part of the call dispatch logic that occurs after the interceptor. If the interceptor logic throws an exception, the remote call will not be made and the client calling code will throw promptly.

The interceptor can be set by setting `GrainClient.ClientInvokeCallback`, which is a property of type `Action<InvokeMethodRequest, IGrain>`. The first argument is the invocation request that includes various details about the invoked call, such as InterfaceId and MethodId, as well as deep-copied arguments. The second argument is the target grain reference to which this call is made.

Currently, the main scenario that we know of that uses client side pre-call inteceptors is to add some extra information to [`Orleans.RequestContext`](Request-Context.md), such as any special call context or token.

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
public class ExceptionConversionFilter : IGrainCallFilter
{
    private static readonly HashSet<string> KnownExceptionTypeAssemblyNames =
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

    public async Task Invoke(IGrainCallContext context)
    {
        var isConversionEnabled =
            RequestContext.Get("IsExceptionConversionEnabled") as bool? == true;
        if (!isConversionEnabled)
        {
            // If exception conversion is not enabled, execute the call without interference.
            await context.Invoke();
            return;
        }
            
        RequestContext.Remove("IsExceptionConversionEnabled");
        try
        {
            await context.Invoke();
        }
        catch (Exception exc)
        {
            var type = exc.GetType();

            if (KnownExceptionTypeAssemblyNames.Contains(type.Assembly.GetName().Name))
            {
                throw;
            }

            // Throw a base exception containing some exception details.
            throw new Exception(
                string.Format(
                    "Exception of non-public type '{0}' has been wrapped."
                    + " Original message: <<<<----{1}{2}{3}---->>>>",
                    type.FullName,
                    Environment.NewLine,
                    exc,
                    Environment.NewLine));
        }
    }
}
```
As mentioned earlier, this filter can then be registered using the `AddGrainCallFilter` extension method:
``` csharp
services.AddGrainCallFilter<ExceptionConversionFilter>();
```
On the client side you have to set up a client-side interceptor:

```csharp
GrainClient.ClientInvokeCallback = (request, grain) =>
    { RequestContext.Set("IsExceptionConversionEnabled", true); };
```

This way the client tells the server that it wants to use exception conversion.

### Calling Grains from Interceptors

It is possible to make grain calls from an interceptor through the injection of `IGrainFactory` into our interceptor class:
``` csharp
private readonly IGrainFactory grainFactory;

public CustomCallFilter(IGrainFactory grainFactory)
{
  this.grainFactory = grainFactory;
}

public async Task Invoke(IGrainCallContext context)
{
  // Hook calls to any grain other than ICustomFilterGrain implementations.
  // This avoids potential infinite recursion when calling OnReceivedCall() below.
  if (!(context.Grain is ICustomFilterGrain))
  {
    var filterGrain = this.grainFactory.GetGrain<ICustomFilterGrain>(context.Grain.GetPrimaryKeyLong());

    // Perform some grain call here.
    await filterGrain.OnReceivedCall();
  }

  // Continue invoking the call on the target grain.
  await context.Invoke();
}
```

# Obsolete Interceptor Features
The following sections describe functionality which has been superseded by the above features and may be removed in a future release.

## Silo-level Interceptors
Silo-level interceptors are called for all grain calls within a silo. They can be installed using `IProviderRuntime.SetInvokeInterceptor(interceptor)`, typically from within a [Bootstrap Provider](Application-Bootstrap-within-a-Silo.md)'s `Init` method, like so:
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
public delegate Task<object> InvokeInterceptor(
    MethodInfo targetMethod,
    InvokeMethodRequest request,
    IGrain target,
    IGrainMethodInvoker invoker);
```

In this delegate:

* `targetMethod` is the `MethodInfo` of the method being called on the grain implementation, not the interface.
* `request.Arguments` is an `object[]` containing the arguments to the method, if any.
* `target` is the grain implementation instance being called.
* `invoker` is used to invoke the method itself.

### Grain-level Interceptors

Grain-level interceptors intercept calls for individual grains only. Grain-level interceptors are enabled by implementing `IGrainInvokeInterceptor` in a grain class:

``` csharp
public interface IGrainInvokeInterceptor
{
    Task<object> Invoke(
        MethodInfo method,
        InvokeMethodRequest request,
        IGrainMethodInvoker invoker);
}
```

For example:

``` csharp
public Task<object> Invoke(
    MethodInfo methodInfo,
    InvokeMethodRequest request,
    IGrainMethodInvoker invoker)
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
