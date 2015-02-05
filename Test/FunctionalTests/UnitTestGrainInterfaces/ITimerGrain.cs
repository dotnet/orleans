using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrains
{
    public interface ITimerGrain : IGrain
    {
        Task StopDefaultTimer();
        Task<TimeSpan> GetTimerPeriod();
        Task<int> GetCounter();
        Task SetCounter(int value);
        Task StartTimer(string timerName);
        Task StopTimer(string timerName);
        Task LongWait(TimeSpan time);
    }

    public interface ITimerPersistantGrain : ITimerGrain
    {
    }

    public interface ITimerCallGrain : IGrain
    {
        Task<int> GetTickCount();
        Task<Exception> GetException();

        Task StartTimer(string name, TimeSpan delay);
        Task StopTimer(string name);
    }
}
