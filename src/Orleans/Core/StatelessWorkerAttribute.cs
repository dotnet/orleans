using System;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The StatelessWorker attribute is used to mark grain class in which there is no expectation
    /// of preservation of grain state between requests and where multiple activations of the same grain are allowed to be created by the runtime. 
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class StatelessWorkerAttribute : Attribute
    {
        /// <summary>
        /// Maximal number of local StatelessWorkers in a single silo.
        /// </summary>
        public int MaxLocalWorkers { get; private set; }

        public StatelessWorkerAttribute(int maxLocalWorkers)
        {
            MaxLocalWorkers = maxLocalWorkers;
        }

        public StatelessWorkerAttribute()
        {
            MaxLocalWorkers = -1;
        }
    }
}