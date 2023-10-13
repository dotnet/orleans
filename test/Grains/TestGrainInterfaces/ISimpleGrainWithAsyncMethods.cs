namespace UnitTests.GrainInterfaces
{
    public interface ISimpleGrainAsync : IGrainWithIntegerKey
    { 
        Task SetA_Async(int a);
        Task SetB_Async(int b);
        Task<int> GetAxB_Async();
        Task<int> GetAxB_Async(int a, int b);
        Task<int> GetA_Async();
        Task IncrementA_Async();
    }

    public interface ISimpleGrainWithAsyncMethods : ISimpleGrainAsync
    {
        Task<int> GetX();
        Task SetX(int x);
    }
}
