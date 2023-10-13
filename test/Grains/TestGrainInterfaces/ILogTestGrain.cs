namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// A grain used for testing log-consistency providers.
    /// The content of this class is pretty arbitrary and messy;
    /// (don't use this as an introduction on how to use JournaledGrain)
    /// it started from SimpleGrain, but a lot of stuff got added over time 
    /// </summary>
    public interface ILogTestGrain: IGrainWithIntegerKey
    {
        // read A

        Task<int> GetAGlobal();

        Task<int> GetALocal();

        // read both

        Task<AB> GetBothGlobal();

        Task<AB> GetBothLocal();

        // reservations

        Task<int[]> GetReservationsGlobal();

        // version

        Task<int> GetConfirmedVersion();

        // set or increment A

        Task SetAGlobal(int a);

        Task<Tuple<int, bool>> SetAConditional(int a);

        Task SetALocal(int a);

        Task IncrementALocal();

        Task IncrementAGlobal();

        // set B

        Task SetBGlobal(int b);

        Task SetBLocal(int b);

        // reservations

        Task AddReservationLocal(int x);

        Task RemoveReservationLocal(int x);


        Task<KeyValuePair<int, object>> Read();
        Task<bool> Update(IReadOnlyList<object> updates, int expectedversion);

        Task<IReadOnlyList<object>> GetEventLog();


        // other operations

            Task SynchronizeGlobalState();
        Task Deactivate();
    }

    /// <summary>
    /// Used by unit tests. 
    /// The fields don't really have any meaning. 
    /// The point of the struct is just that a grain method can return both A and B at the same time.
    /// </summary>
    [GenerateSerializer]
    public struct AB
    {
        [Id(0)]
        public int A;

        [Id(1)]
        public int B;
    }
}
