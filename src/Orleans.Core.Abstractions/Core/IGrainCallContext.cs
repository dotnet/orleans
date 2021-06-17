using System;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans
{
    /// <summary>
    /// A delegate used to intercept invocation of a request.
    /// </summary>
    /// <param name="context">The invocation context.</param>
    /// <returns>A <see cref="Task"/> which must be awaited before processing continues.</returns>
    public delegate Task GrainCallFilterDelegate(IGrainCallContext context);

    /// <summary>
    /// A delegate used to intercept an incoming request.
    /// </summary>
    /// <param name="context">The invocation context.</param>
    /// <returns>A <see cref="Task"/> which must be awaited before processing continues.</returns>
    public delegate Task OutgoingGrainCallFilterDelegate(IOutgoingGrainCallContext context);

    /// <summary>
    /// A delegate used to intercept an outgoing request.
    /// </summary>
    /// <param name="context">The invocation context.</param>
    /// <returns>A <see cref="Task"/> which must be awaited before processing continues.</returns>
    public delegate Task IncomingGrainCallFilterDelegate(IIncomingGrainCallContext context);

    /// <summary>
    /// Represents a method invocation as well as the result of invocation.
    /// </summary>
    public interface IGrainCallContext
    {
        /// <summary>
        /// Gets the grain being invoked.
        /// </summary>
        IAddressable Grain { get; }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> of the method being invoked.
        /// </summary>
        [Obsolete("Use InterfaceMethod or IIncomingGrainCallContext.ImplementationMethod instead.")]
        MethodInfo Method { get; }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> for the interface method being invoked.
        /// </summary>
        MethodInfo InterfaceMethod { get; }

        /// <summary>
        /// Gets the arguments for this method invocation.
        /// </summary>
        IMethodArguments Arguments { get; }

        /// <summary>
        /// Invokes the request.
        /// </summary>
        Task Invoke();

        /// <summary>
        /// Gets or sets the result.
        /// </summary>
        object Result { get; set; }
       
        /// <summary>
        /// Gets or sets the response.
        /// </summary>
        Response Response { get; set; }
    }

    /// <summary>
    /// Represents an incoming method invocation as well as the result of invocation.
    /// </summary>
    public interface IIncomingGrainCallContext : IGrainCallContext
    {
        /// <summary>
        /// Gets the <see cref="MethodInfo"/> for the implementation method being invoked.
        /// </summary>
        MethodInfo ImplementationMethod { get; }
    }

    /// <summary>
    /// Represents an outgoing method invocation as well as the result of invocation.
    /// </summary>
    public interface IOutgoingGrainCallContext : IGrainCallContext
    {
    }

    public interface IMethodArguments
    {
        object this[int index] { get;set; }
        T GetArgument<T>(int index);
        void SetArgument<T>(int index, T value);
        int Length { get; }
    }
}
