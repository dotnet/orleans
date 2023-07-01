namespace TestGrainInterfaces
{
    public interface IDoSomething
    {
        Task<string> DoIt();

        Task SetA(int a);

        Task IncrementA();

        Task<int> GetA();
    }

    public interface IDoSomethingWithMoreGrain : IDoSomething, IGrainWithIntegerKey
    {
        Task<string> DoThat();

        Task SetB(int a);

        Task IncrementB();

        Task<int> GetB();
    }

    public interface IDoSomethingEmptyGrain : IDoSomething, IGrainWithIntegerKey
    {
    }

    public interface IDoSomethingEmptyWithMoreGrain : IDoSomethingEmptyGrain
    {
        Task<string> DoMore();
    }

    public interface IDoSomethingWithMoreEmptyGrain : IDoSomethingEmptyWithMoreGrain
    {
    }

    public interface IDoSomethingCombinedGrain : IDoSomethingWithMoreGrain, IDoSomethingWithMoreEmptyGrain
    {
        Task SetC(int a);

        Task IncrementC();

        Task<int> GetC();
    }

}
