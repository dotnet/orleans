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
        /// Gets the request.
        /// </summary>
        IInvokable Request { get; }

        /// <summary>
        /// Gets the grain being invoked.
        /// </summary>
        object Grain { get; }

        /// <summary>
        /// Gets the identity of the source, if available.
        /// </summary>
        GrainId? SourceId { get; }

        /// <summary>
        /// Gets the identity of the target.
        /// </summary>
        GrainId TargetId { get; }

        /// <summary>
        /// Gets the type of the interface being invoked.
        /// </summary>
        GrainInterfaceType InterfaceType { get; }

        /// <summary>
        /// Gets the name of the interface being invoked.
        /// </summary>
        string InterfaceName { get; }

        /// <summary>
        /// Gets the name of the method being invoked.
        /// </summary>
        string MethodName { get; }

        /// <summary>
        /// Gets the <see cref="MethodInfo"/> for the interface method being invoked.
        /// </summary>
        MethodInfo InterfaceMethod { get; }

        /// <summary>
        /// Gets or sets the result.
        /// </summary>
        object Result { get; set; }
       
        /// <summary>
        /// Gets or sets the response.
        /// </summary>
        Response Response { get; set; }

        /// <summary>
        /// Invokes the request.
        /// </summary>
        /// <returns>
        /// A <see cref="Task"/> representing the invocation.
        /// </returns>
        Task Invoke();
    }

    /// <summary>
    /// Represents an incoming method invocation as well as the result of invocation.
    /// </summary>
    public interface IIncomingGrainCallContext : IGrainCallContext
    {
        /// <summary>
        /// Gets the grain context of the target.
        /// </summary>
        public IGrainContext TargetContext { get; }

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
        /// <summary>
        /// Gets the grain context of the sender.
        /// </summary>
        public IGrainContext SourceContext { get; }
    }
}
