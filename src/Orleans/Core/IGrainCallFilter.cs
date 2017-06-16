using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Interface for grain call filters.
    /// </summary>
    public interface IGrainCallFilter
    {
        /// <summary>
        /// Invokes this filter.
        /// </summary>
        /// <param name="context">The grain call context.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Invoke(IGrainCallContext context);
    }
}