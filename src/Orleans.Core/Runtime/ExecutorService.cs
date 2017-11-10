using System.Threading.Tasks;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    internal class ExecutorService : ITaskScheduler
    {
        private readonly TaskScheduler taskScheduler = new ThreadPerTaskScheduler(task => (task as AsynchAgentTask)?.Name);

        public void RunTask(Task task)
        {
            task.Start(taskScheduler);
        }
    }
}