using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ITimerGrain : IGrainWithIntegerKey
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

    public interface ITimerCallGrain : IGrainWithIntegerKey
    {
        Task<int> GetTickCount();
        Task<Exception> GetException();

        Task StartTimer(string name, TimeSpan delay);
        Task StopTimer(string name);
    }
}
