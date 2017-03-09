using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// A utility interface that allows to control the rate of generation of asynchronous activities.
    /// </summary>
    /// <seealso cref="AsyncPipeline"/>   
    public interface IPipeline
    {
        /// <summary>Adds a new task to the pipeline</summary>
        /// <param name="task">The task to add</param>
        void Add(Task task);

        /// <summary>Waits until all currently queued asynchronous operations are done. Blocks the calling thread.</summary>
        void Wait();

        /// <summary>The number of items currently enqueued into this pipeline.</summary>
        int Count { get; }
    }
}