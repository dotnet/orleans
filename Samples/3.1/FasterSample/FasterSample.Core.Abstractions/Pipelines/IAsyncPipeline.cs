using System;
using System.Threading;
using System.Threading.Tasks;

namespace FasterSample.Core.Pipelines
{
    public interface IAsyncPipeline
    {
        /// <summary>
        /// Adds an action to the pipeline and returns the task for it once it is started.
        /// If there is enough capacity then the action will be started immediately and this method will also complete immediately.
        /// If there is not enough capacity then this method will wait until there is enough capacity before starting the action.
        /// This behaviour makes it safe to use this method in an long-lived loop that keeps generating new actions.
        /// The pipeline itself will handle action parallelism as needed.
        /// </summary>
        /// <param name="action">The action to add to the pipeline.</param>
        /// <param name="cancellationToken">A token that is used to cancel the addition of the task into the pipeline.</param>
        Task<Task> AddAsync(Func<Task> action, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns a task that completes when all currently started tasks complete.
        /// </summary>
        Task WhenAll();
    }
}