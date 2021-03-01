using System;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Interface for incoming grain call filters.
    /// </summary>
    public interface IIncomingGrainCallFilter
    {
        /// <summary>
        /// Invokes this filter.
        /// </summary>
        /// <param name="context">The grain call context.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Invoke(IIncomingGrainCallContext context);
    }

    /// <summary>
    /// Interface for outgoing grain call filters.
    /// </summary>
    public interface IOutgoingGrainCallFilter
    {
        /// <summary>
        /// Invokes this filter.
        /// </summary>
        /// <param name="context">The grain call context.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Invoke(IOutgoingGrainCallContext context);
    }
}