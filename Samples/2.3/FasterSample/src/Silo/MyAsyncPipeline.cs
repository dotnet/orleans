using System.Collections.Generic;
using System.Threading.Tasks;

namespace Silo
{
    /// <summary>
    /// Implements a lightweight uniform workload pipeline.
    /// This pipeline assumes all work items are of equal length and will await tasks in order.
    /// </summary>
    public class MyAsyncPipeline
    {
        private readonly int capacity;
        private readonly Queue<Task> queue;

        public MyAsyncPipeline(int capacity)
        {
            this.capacity = capacity;
            this.queue = new Queue<Task>(capacity);
        }

        /// <summary>
        /// Awaits until there is a slot free.
        /// This allows the user to decide whether to always keep under capacity (by calling this before running a task and calling <see cref="Add(Task)"/>)
        /// or to keep an extra task in-flight (by not calling this before <see cref="Add(Task)"/>)
        /// </summary>
        public void WaitOne()
        {
            while (queue.Count >= capacity)
            {
                queue.Dequeue().Wait();
            }
        }

        /// <summary>
        /// Adds a running task to the pipeline.
        /// Will await until there is a slot free.
        /// </summary>
        public void Add(Task task)
        {
            WaitOne();
            queue.Enqueue(task);
        }

        /// <summary>
        /// Awaits for all running tasks to complete.
        /// </summary>
        public void WaitAll()
        {
            while (queue.Count > 0)
            {
                queue.Dequeue().Wait();
            }
        }
    }
}