using System.Reflection;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// A delegate used to intercept invocation of a request.
    /// </summary>
    /// <param name="context">The invocation context.</param>
    /// <returns>A <see cref="Task"/> which must be awaited before processing continues.</returns>
    public delegate Task GrainCallFilterDelegate(IGrainCallContext context);

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
}
