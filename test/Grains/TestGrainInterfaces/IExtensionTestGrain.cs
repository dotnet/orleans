namespace UnitTests.GrainInterfaces
{
    public interface IExtensionTestGrain : IGrainWithIntegerKey
    {
        Task InstallExtension(string name);
    }

    public interface IGenericExtensionTestGrain<in T> : IGrainWithIntegerKey
    {
        Task InstallExtension(T name);
    }

    public interface IGenericGrainWithNonGenericExtension<in T> : IGrainWithIntegerKey
    {
        Task DoSomething();
    }

    public interface INoOpTestGrain : IGrainWithIntegerKey
    {
    }
}