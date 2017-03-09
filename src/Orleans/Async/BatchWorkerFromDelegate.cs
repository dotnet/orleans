using System;
using System.Threading.Tasks;

namespace Orleans
{
    /// A convenient variant of a batch worker 
    /// that allows the work function to be passed as a constructor argument
    public class BatchWorkerFromDelegate : BatchWorker
    {
        public BatchWorkerFromDelegate(Func<Task> work)
        {
            this.work = work;
        }

        private Func<Task> work;

        protected override Task Work()
        {
            return work();
        }
    }
}