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
        object CreateInstance(IGrainContext context);

        /// <summary>
        /// Disposes the provided grain instance which is associated with the provided grain context.
        /// </summary>
        ValueTask DisposeInstance(IGrainContext context, object instance);
    }
}