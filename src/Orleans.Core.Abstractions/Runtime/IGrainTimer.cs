using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public interface IGrainTimer : IDisposable
    {
        void Start();

        void Stop();

        Task GetCurrentlyExecutingTickTask();

        void Change(TimeSpan dueTime, TimeSpan period);
    }
}