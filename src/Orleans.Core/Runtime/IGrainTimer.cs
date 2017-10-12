using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal interface IGrainTimer : IDisposable
    {
        void Start();

        void Stop();

        Task GetCurrentlyExecutingTickTask();
    }
}