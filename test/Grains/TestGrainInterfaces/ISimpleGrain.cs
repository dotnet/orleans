namespace UnitTests.GrainInterfaces
{
    public interface ISimpleGrain : IGrainWithIntegerKey
    {
        Task SetA(int a);
        Task SetB(int b);
        Task IncrementA();
        Task<int> GetAxB();
        Task<int> GetAxB(int a, int b);
        Task<int> GetA();
    }
}
