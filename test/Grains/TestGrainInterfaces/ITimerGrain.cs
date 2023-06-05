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
        Task Deactivate();
    }

    public interface ITimerCallGrain : IGrainWithIntegerKey
    {
        Task<int> GetTickCount();
        Task<Exception> GetException();

        Task StartTimer(string name, TimeSpan delay);
        Task StopTimer(string name);
    }

    public interface ITimerRequestGrain : IGrainWithIntegerKey
    {
        Task StartAndWaitTimerTick(TimeSpan dueTime);

        Task StartStuckTimer(TimeSpan dueTime);

        Task<string> GetRuntimeInstanceId();
    }
}
