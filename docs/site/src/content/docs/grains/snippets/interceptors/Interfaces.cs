// These are simplified interface definitions shown for documentation purposes.
// The actual interfaces are defined in Orleans.Core.Abstractions and cannot be
// redefined in user code. These snippets demonstrate the API shape.

// NOTE: These interfaces are shown in the documentation to explain the API.
// They are wrapped in #if false to prevent compilation conflicts with Orleans
// types while still being extractable by the documentation build system.

#if SHOW_INTERFACE_DEFINITIONS
using System.Reflection;
using Orleans;

namespace Orleans;

// <incoming_filter_interface>
public interface IIncomingGrainCallFilter
{
    Task Invoke(IIncomingGrainCallContext context);
}
// </incoming_filter_interface>

// <incoming_context_interface>
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
// </incoming_context_interface>

// <outgoing_filter_interface>
public interface IOutgoingGrainCallFilter
{
    Task Invoke(IOutgoingGrainCallContext context);
}
// </outgoing_filter_interface>

// <outgoing_context_interface>
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
// </outgoing_context_interface>
#endif
