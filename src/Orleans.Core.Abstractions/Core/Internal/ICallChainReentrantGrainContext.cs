using System;
using Orleans.Runtime;

namespace Orleans.Core.Internal
{
    /// <summary>
    /// Provides functionality for entering and exiting sections of code within a grain during which requests bearing the same <see cref="RequestContext.ReentrancyId"/> are allowed to re-enter the grain.
    /// </summary>
    public interface ICallChainReentrantGrainContext
    {
        /// <summary>
        /// Marks the beginning of a section of code within a grain during which requests bearing the same <see cref="RequestContext.ReentrancyId"/> are allowed to re-enter the grain.
        /// </summary>
        void OnEnterReentrantSection(Guid reentrancyId);

        /// <summary>
        /// Marks the end of a section of code within a grain during which requests bearing the same <see cref="RequestContext.ReentrancyId"/> are allowed to re-enter the grain.
        /// </summary>
        void OnExitReentrantSection(Guid reentrancyId);
    }
}
