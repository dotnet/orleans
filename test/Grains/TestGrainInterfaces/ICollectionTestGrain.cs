namespace UnitTests.GrainInterfaces
{
    public interface ICollectionTestGrain : IGrainWithIntegerKey
    {
        Task<TimeSpan> GetAge();

        Task<int> IncrCounter();

        Task DeactivateSelf();

        Task SetOther(ICollectionTestGrain other);

        Task<TimeSpan> GetOtherAge();

        Task<ICollectionTestGrain> GetGrainReference();

        Task<string> GetRuntimeInstanceId();

        Task StartTimer(TimeSpan timerPeriod, TimeSpan delayPeriod);
    }
}
