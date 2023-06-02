using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// Creates a grain instance for a given grain context.
    /// </summary>
    public interface IGrainActivator
    {
        /// <summary>
        /// Returns a new grain instance for the provided grain context.
        /// </summary>
        /// <param name="context">The grain context.</param>
        /// <returns>The grain instance.</returns>
        object CreateInstance(IGrainContext context);

        /// <summary>
        /// Disposes the provided grain instance which is associated with the provided grain context.
        /// </summary>
        /// <param name="context">The grain context.</param>
        /// <param name="instance">The grain instance.</param>
        /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
        ValueTask DisposeInstance(IGrainContext context, object instance);
    }
}