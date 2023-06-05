namespace UnitTests.GrainInterfaces
{
    public interface IBase : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IDerivedFromBase : IBase
    {
        Task<bool> Bar();
    }

    public interface IBase1 : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IBase2 : IGrainWithIntegerKey
    {
        Task<bool> Bar();
    }

    public interface IBase3 : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IBase4 : IGrainWithIntegerKey
    {
        Task<bool> Foo();
    }

    public interface IStringGrain : IGrainWithStringKey
    {
        Task<bool> Foo();
    }

    public interface IGuidGrain : IGrainWithGuidKey
    {
        Task<bool> Foo();
    }
}
