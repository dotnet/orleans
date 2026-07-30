---
title: Grain call filters
description: Learn about grain call filters in .NET Orleans.
ms.date: 01/21/2026
ms.topic: concept-article
---

# Grain call filters

Grain call filters provide a way to intercept grain calls. Filters can execute code both before and after a grain call. You can install multiple filters simultaneously. Filters are asynchronous and can modify <xref:Orleans.Runtime.RequestContext>, arguments, and the return value of the method being invoked. Filters can also inspect the <xref:System.Reflection.MethodInfo> of the method being invoked on the grain class and can be used to throw or handle exceptions.

Some example uses of grain call filters are:

- **Authorization**: A filter can inspect the method being invoked and the arguments, or authorization information in the <xref:Orleans.Runtime.RequestContext>, to determine whether to allow the call to proceed.
- **Logging/Telemetry**: A filter can log information and capture timing data and other statistics about method invocation.
- **Error Handling**: A filter can intercept exceptions thrown by a method invocation and transform them into other exceptions or handle the exceptions as they pass through the filter.

Filters come in two types:

- Incoming call filters
- Outgoing call filters

Incoming call filters are executed when receiving a call. Outgoing call filters are executed when making a call.

## Incoming call filters

Incoming grain call filters implement the <xref:Orleans.IIncomingGrainCallFilter> interface, which has one method:

```csharp
public interface IIncomingGrainCallFilter
{
    Task Invoke(IIncomingGrainCallContext context);
}
```

The <xref:Orleans.IIncomingGrainCallContext> argument passed to the `Invoke` method has the following shape:

```csharp
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

The `IIncomingGrainCallFilter.Invoke(IIncomingGrainCallContext)` method must `await` or return the result of `IIncomingGrainCallContext.Invoke()` to execute the next configured filter and eventually the grain method itself. You can modify the `Result` property after awaiting the `Invoke()` method. The `ImplementationMethod` property returns the <xref:System.Reflection.MethodInfo> of the implementation class. You can access the <xref:System.Reflection.MethodInfo> of the interface method using the `InterfaceMethod` property. Grain call filters are called for all method calls to a grain, including calls to grain extensions (implementations of `IGrainExtension`) installed in the grain. For example, Orleans uses grain extensions to implement Streams and Cancellation Tokens. Therefore, expect that the value of `ImplementationMethod` isn't always a method in the grain class itself.

## Configure incoming grain call filters

You can register implementations of <xref:Orleans.IIncomingGrainCallFilter> either as silo-wide filters via Dependency Injection or as grain-level filters by having a grain implement `IIncomingGrainCallFilter` directly.

### Silo-wide grain call filters

You can register a delegate as a silo-wide grain call filter using Dependency Injection like this:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="silo_delegate_filter":::

Similarly, you can register a class as a grain call filter using the <xref:Orleans.Hosting.GrainCallFilterSiloBuilderExtensions.AddIncomingGrainCallFilter*> helper method. Here's an example of a grain call filter that logs the results of every grain method:

:::code language="csharp" source="snippets/interceptors/LoggingFilters.cs" id="logging_incoming_filter":::

This filter can then be registered using the `AddIncomingGrainCallFilter` extension method:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_incoming_filter":::

Alternatively, the filter can be registered without the extension method:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_incoming_filter_di":::

### Per-grain grain call filters

A grain class can register itself as a grain call filter and filter any calls made to it by implementing `IIncomingGrainCallFilter` like this:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="per_grain_filter":::

In the preceding example, all calls to the `GetFavoriteNumber` method return `38` instead of `7` because the filter altered the return value.

Another use case for filters is access control, as shown in this example:

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="access_control_attribute":::

:::code language="csharp" source="snippets/interceptors/GrainFilters.cs" id="access_control_grain":::

In the preceding example, the `SpecialAdminOnlyOperation` method can only be called if `"isAdmin"` is set to `true` in the <xref:Orleans.Runtime.RequestContext>. In this way, you can use grain call filters for authorization. In this example, it's the caller's responsibility to ensure the `"isAdmin"` value is set correctly and that authentication is performed correctly. Note that the `[AdminOnly]` attribute is specified on the grain class method. This is because the `ImplementationMethod` property returns the <xref:System.Reflection.MethodInfo> of the implementation, not the interface. The filter could also check the `InterfaceMethod` property.

## Grain call filter ordering

Grain call filters follow a defined order:

1. `IIncomingGrainCallFilter` implementations configured in the dependency injection container, in the order in which they are registered.
1. Grain-level filter, if the grain implements `IIncomingGrainCallFilter`.
1. Grain method implementation or grain extension method implementation.

Each call to `IIncomingGrainCallContext.Invoke()` encapsulates the next defined filter, allowing each filter a chance to execute code before and after the next filter in the chain and, eventually, the grain method itself.

## Outgoing call filters

Outgoing grain call filters are similar to incoming grain call filters. The major difference is that they are invoked on the caller (client) rather than the callee (grain).

Outgoing grain call filters implement the `IOutgoingGrainCallFilter` interface, which has one method:

```csharp
public interface IOutgoingGrainCallFilter
{
    Task Invoke(IOutgoingGrainCallContext context);
}
```

The <xref:Orleans.IOutgoingGrainCallContext> argument passed to the `Invoke` method has the following shape:

```csharp
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

The `IOutgoingGrainCallFilter.Invoke(IOutgoingGrainCallContext)` method must `await` or return the result of `IOutgoingGrainCallContext.Invoke()` to execute the next configured filter and eventually the grain method itself. You can modify the `Result` property after awaiting the `Invoke()` method. You can access the <xref:System.Reflection.MethodInfo> of the interface method being called using the `InterfaceMethod` property. Outgoing grain call filters are invoked for all method calls to a grain, including calls to system methods made by Orleans.

## Configure outgoing grain call filters

You can register implementations of `IOutgoingGrainCallFilter` on both silos and clients using Dependency Injection.

Register a delegate as a call filter like this:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="outgoing_delegate_filter":::

In the above code, `builder` may be either an instance of <xref:Orleans.Hosting.ISiloBuilder> or <xref:Orleans.IClientBuilder>.

Similarly, you can register a class as an outgoing grain call filter. Here's an example of a grain call filter that logs the results of every grain method:

:::code language="csharp" source="snippets/interceptors/LoggingFilters.cs" id="logging_outgoing_filter":::

This filter can then be registered using the `AddOutgoingGrainCallFilter` extension method:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_outgoing_filter":::

Alternatively, the filter can be registered without the extension method:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_outgoing_filter_di":::

As with the delegate call filter example, `builder` may be an instance of either <xref:Orleans.Hosting.ISiloBuilder> or <xref:Orleans.IClientBuilder>.

## Use cases

### Exception conversion

When an exception thrown from the server is deserialized on the client, you might sometimes get the following exception instead of the actual one: `TypeLoadException: Could not find Whatever.dll.`

This happens if the assembly containing the exception isn't available to the client. For example, say you use Entity Framework in your grain implementations; an `EntityException` might be thrown. The client, on the other hand, doesn't (and shouldn't) reference `EntityFramework.dll` since it doesn't know the underlying data access layer.

When the client tries to deserialize the `EntityException`, it fails due to the missing DLL. As a consequence, a <xref:System.TypeLoadException> is thrown, hiding the original `EntityException`.

One might argue this is acceptable since the client would never handle the `EntityException`; otherwise, it would need to reference `EntityFramework.dll`.

But what if the client wants at least to log the exception? The problem is that the original error message is lost. One way to work around this issue is to intercept server-side exceptions and replace them with plain exceptions of type <xref:System.Exception> if the exception type is presumably unknown on the client-side.

However, keep one important thing in mind: you only want to replace an exception **if the caller is the grain client**. You don't want to replace an exception if the caller is another grain (or the Orleans infrastructure making grain calls, for example,, on the `GrainBasedReminderTable` grain).

On the server-side, you can do this with a silo-level interceptor:

:::code language="csharp" source="snippets/interceptors/ExceptionConversion.cs" id="exception_conversion_filter":::

This filter can then be registered on the silo:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="register_exception_filter":::

Enable the filter for calls made by the client by adding an outgoing call filter:

:::code language="csharp" source="snippets/interceptors/Configuration.cs" id="client_exception_filter":::

This way, the client tells the server it wants to use exception conversion.

### Call grains from interceptors

You can make grain calls from an interceptor by injecting <xref:Orleans.IGrainFactory> into the interceptor class:

:::code language="csharp" source="snippets/interceptors/GrainFactoryInjection.cs" id="grain_factory_injection":::
