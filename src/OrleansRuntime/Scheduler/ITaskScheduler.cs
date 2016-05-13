using System.Threading.Tasks;


namespace Orleans.Runtime.Scheduler
{
    internal interface ITaskScheduler
    {
        void RunTask(Task task);
    }
}
