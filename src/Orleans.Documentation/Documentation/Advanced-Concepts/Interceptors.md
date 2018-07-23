---
layout: page
title: Grain Call Filters
---

# Grain Call Filters

Grain call filters provide a means for intercepting grain calls. Filters can execute code both before and after a grain call. Multiple filters can be installed simultaneously. Filters are asynchronous and can modify `RequestContext`, arguments, and the return value of the method being invoked. Filters can also inspect the `MethodInfo` of the method being invoked on the grain class and can be used to throw or handle exceptions.

Some example usages of grain call filters are:

* Authorization: a filter can inspect the method being invoked and the arguments or some authorization information in the `RequestContext` to determine whether or not to allow the call to proceed.
* Logging/Telemetry: a filter can log information and capture timing data and other statistics about method invocation.
* Error Handling: a filter can intercept exceptions thrown by a method invocation and transform it into another exception or handle the exception as it passes through the filter.

Filters come in two flavors:

* Incoming call filters
* Outgoing call filters

Incoming call filters are executed when receiving a call. Outgoing call filters are executed when making a call.

# Incoming Call Filters

Incoming grain call filters implement the `IIncomingGrainCallFilter` interface, which has one method:

``` csharp
public interface IIncomingGrainCallFilter
{
    Task Invoke(IIncomingGrainCallContext context);
}
```

The `IIncomingGrainCallContext` argument passed to the `Invoke` method has the following shape:

``` csharp
public interface IIncomingGrainCallContext
{
    /// <summary>
    /// Gets the grain being invoked.
    /// </summary>
    IAddressable Grain { get; }

    /// <summary>
    /// Gets the <see cref="MethodInfo"/> for the interface method being invoked.
    /// </summary>
    MethodInfo InterfaceMethod { get; }

    /// <summary>
    /// Gets the <see cref="MethodInfo"/> for the implementation method being invoked.
    /// </summary>
    MethodInfo ImplementationMethod { get; }

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

The `IIncomingGrainCallFilter.Invoke(IIncomingGrainCallContext)` method must await or return the result of `IIncomingGrainCallContext.Invoke()` to execute the next configured filter and eventually the grain method itself. The `Result` property can be modified after awaiting the `Invoke()` method. The `ImplementationMethod` property returns the `MethodInfo` of the implementation class. The `MethodInfo` of the interface method can be accessed using the `InterfaceMethod` property. Grain call filters are called for all method calls to a grain and this includes calls to grain extensions (implementations of `IGrainExtension`) which are installed in the grain. For example, grain extensions are used to implement Streams and Cancellation Tokens. Therefore, it should be expected that the value of `ImplementationMethod` is not always a method in the grain class itself.

## Configuring Incoming Grain Call Filters

Implementations of `IIncomingGrainCallFilter` can either be registered as silo-wide filters via Dependency Injection or they can be registered as grain-level filters via a grain implementing `IIncomingGrainCallFilter` directly.

### Silo-wide Grain Call Filters

A delegate can be registered as a silo-wide grain call filters using Dependency Injection like so:

``` csharp
siloHostBuilder.AddIncomingGrainCallFilter(async context =>
{
    // If the method being called is 'MyInterceptedMethod', then set a value
    // on the RequestContext which can then be read by other filters or the grain.
    if (string.Equals(context.InterfaceMethod.Name, nameof(IMyGrain.MyInterceptedMethod)))
    {
        RequestContext.Set("intercepted value", "this value was added by the filter");
    }

    await context.Invoke();

    // If the grain method returned an int, set the result to double that value.
    if (context.Result is int resultValue) context.Result = resultValue * 2;
});
```

Similarly, a class can be registered as a grain call filter using the `AddIncomingGrainCallFilter` helper method.
Here is an example of a grain call filter which logs the results of every grain method:

```csharp
public class LoggingCallFilter : IIncomingGrainCallFilter
{
    private readonly Logger log;

    public LoggingCallFilter(Factory<string, Logger> loggerFactory)
    {
        this.log = loggerFactory(nameof(LoggingCallFilter));
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        try
        {
            await context.Invoke();
            var msg = string.Format(
                "{0}.{1}({2}) returned value {3}",
                context.Grain.GetType(),
                context.InterfaceMethod.Name,
                string.Join(", ", context.Arguments),
                context.Result);
            this.log.Info(msg);
        }
        catch (Exception exception)
        {
            var msg = string.Format(
                "{0}.{1}({2}) threw an exception: {3}",
                context.Grain.GetType(),
                context.InterfaceMethod.Name,
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

This filter can then be registered using the `AddIncomingGrainCallFilter` extension method:

``` csharp
siloHostBuilder.AddIncomingGrainCallFilter<LoggingCallFilter>();
```

Alternatively, the filter can be registered without the extension method:

``` csharp
siloHostBuilder.ConfigureServices(
    services => services.AddSingleton<IIncomingGrainCallFilter, LoggingCallFilter>());
```

### Per-grain Grain Call Filters

A grain class can register itself as a grain call filter and filter any calls made to it by implementing `IIncomingGrainCallFilter` like so:

```csharp
public class MyFilteredGrain : Grain, IMyFilteredGrain, IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        await context.Invoke();

        // Change the result of the call from 7 to 38.
        if (string.Equals(context.InterfaceMethod.Name, nameof(this.GetFavoriteNumber)))
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

public class MyAccessControlledGrain : Grain, IMyFilteredGrain, IIncomingGrainCallFilter
{
    public Task Invoke(IIncomingGrainCallContext context)
    {
        // Check access conditions.
        var isAdminMethod = context.ImplementationMethod.GetCustomAttribute<AdminOnlyAttribute>();
        if (isAdminMethod && !(bool) RequestContext.Get("isAdmin"))
        {
            throw new AccessDeniedException($"Only admins can access {context.ImplementationMethod.Name}!");
        }

        return context.Invoke();
    }

    [AdminOnly]
    public Task<int> SpecialAdminOnlyOperation() => Task.FromResult(7);
}
```

In the above example, the `SpecialAdminOnlyOperation` method can only be called if `"isAdmin"` is set to `true` in the [`RequestContext`](Request-Context.md). In this way, grain call filters can be used for authorization. In this example, it is the responsibility of the caller to ensure that the `"isAdmin"` value is set correctly and that authentication is performed correctly. Note that the `[AdminOnly]` attribute is specified on the grain class method. This is because the `ImplementationMethod` property returns the `MethodInfo` of the implementation, not the interface. The filter could also check the `InterfaceMethod` property.

## Ordering of Grain Call Filters

Grain call filters follow a defined ordering:

1. `IIncomingGrainCallFilter` implementations configured in the dependency injection container, in the order in which they are registered.
2. Grain-level filter, if the grain implements `IIncomingGrainCallFilter`.
3. Grain method implementation or grain extension method implementation.

Each call to `IIncomingGrainCallContext.Invoke()` encapsulates the next defined filter so that each filter has a chance to execute code before and after the next filter in the chain and eventually the grain method itself.

# Outgoing Call Filters

Outgoing grain call filters are similar to incoming grain call filters with the major difference being that they are invoked on the caller (client) rather than the callee (grain).

Outgoing grain call filters implement the `IOutgoingGrainCallFilter` interface, which has one method:

``` csharp
public interface IOutgoingGrainCallFilter
{
    Task Invoke(IOutgoingGrainCallContext context);
}
```

The `IOutgoingGrainCallContext` argument passed to the `Invoke` method has the following shape:

``` csharp
public interface IOutgoingGrainCallContext
{
    /// <summary>
    /// Gets the grain being invoked.
    /// </summary>
    IAddressable Grain { get; }

    /// <summary>
    /// Gets the <see cref="MethodInfo"/> for the interface method being invoked.
    /// </summary>
    MethodInfo InterfaceMethod { get; }

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

The `IOutgoingGrainCallFilter.Invoke(IOutgoingGrainCallContext)` method must await or return the result of `IOutgoingGrainCallContext.Invoke()` to execute the next configured filter and eventually the grain method itself. The `Result` property can be modified after awaiting the `Invoke()` method. The `MethodInfo` of the interface method being called can be accessed using the `InterfaceMethod` property. Outgoing grain call filters are invoked for all method calls to a grain and this includes calls to system methods made by Orleans.

## Configuring Outgoing Grain Call Filters

Implementations of `IOutgoingGrainCallFilter` can either be registered on both silos and clients using Dependency Injection.

A delegate can be registered as a call filter like so:

``` csharp
builder.AddOutgoingGrainCallFilter(async context =>
{
    // If the method being called is 'MyInterceptedMethod', then set a value
    // on the RequestContext which can then be read by other filters or the grain.
    if (string.Equals(context.InterfaceMethod.Name, nameof(IMyGrain.MyInterceptedMethod)))
    {
        RequestContext.Set("intercepted value", "this value was added by the filter");
    }

    await context.Invoke();

    // If the grain method returned an int, set the result to double that value.
    if (context.Result is int resultValue) context.Result = resultValue * 2;
});
```

In the above code, `builder` may be either an instance of `ISiloHostBuilder` or `IClientBuilder`.

Similarly, a class can be registered as an outgoing grain call filter.
Here is an example of a grain call filter which logs the results of every grain method:

```csharp
public class LoggingCallFilter : IOutgoingGrainCallFilter
{
    private readonly Logger log;

    public LoggingCallFilter(Factory<string, Logger> loggerFactory)
    {
        this.log = loggerFactory(nameof(LoggingCallFilter));
    }

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        try
        {
            await context.Invoke();
            var msg = string.Format(
                "{0}.{1}({2}) returned value {3}",
                context.Grain.GetType(),
                context.InterfaceMethod.Name,
                string.Join(", ", context.Arguments),
                context.Result);
            this.log.Info(msg);
        }
        catch (Exception exception)
        {
            var msg = string.Format(
                "{0}.{1}({2}) threw an exception: {3}",
                context.Grain.GetType(),
                context.InterfaceMethod.Name,
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

This filter can then be registered using the `AddOutgoingGrainCallFilter` extension method:

``` csharp
builder.AddOutgoingGrainCallFilter<LoggingCallFilter>();
```

Alternatively, the filter can be registered without the extension method:

``` csharp
builder.ConfigureServices(
    services => services.AddSingleton<IOutgoingGrainCallFilter, LoggingCallFilter>());
```

As with the delegate call filter example, `builder` may be an instance of either `ISiloHostBuiler` or `IClientBuilder`.

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
public class ExceptionConversionFilter : IIncomingGrainCallFilter
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

    public async Task Invoke(IIncomingGrainCallContext context)
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

This filter can then be registered on the silo:

``` csharp
siloHostBuilder.AddIncomingGrainCallFilter<ExceptionConversionFilter>();
```

Enable the filter for calls made by the client by adding an outgoing call filter:

```csharp
clientBuilder.AddOutgoingGrainCallFilter(context =>
{
    RequestContext.Set("IsExceptionConversionEnabled", true);
    return context.Invoke();
});
```

This way the client tells the server that it wants to use exception conversion.

### Calling Grains from Interceptors

It is possible to make grain calls from an interceptor by injecting `IGrainFactory` into the interceptor class:

``` csharp
private readonly IGrainFactory grainFactory;

public CustomCallFilter(IGrainFactory grainFactory)
{
  this.grainFactory = grainFactory;
}

public async Task Invoke(IIncomingGrainCallContext context)
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