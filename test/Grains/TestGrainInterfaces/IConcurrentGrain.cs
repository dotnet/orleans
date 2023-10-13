namespace UnitTests.GrainInterfaces
{
    public interface IConcurrentGrain : IGrainWithIntegerKey
    {
        Task Initialize(int index);

        //[ReadOnly]
        Task<int> A();
        //[ReadOnly]
        Task<int> B(int time);

        Task<List<int>> ModifyReturnList_Test();

        Task Initialize_2(int index);
        Task<int> TailCall_Caller(IConcurrentReentrantGrain another, bool doCW);
        Task<int> TailCall_Resolver(IConcurrentReentrantGrain another);
    }

    public interface IConcurrentReentrantGrain : IGrainWithIntegerKey
    {
        Task Initialize_2(int index);
        Task<int> TailCall_Called();
        Task<int> TailCall_Resolve();
    }
}
