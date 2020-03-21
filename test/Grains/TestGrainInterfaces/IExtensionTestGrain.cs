using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IExtensionTestGrain : IGrainWithIntegerKey
    {
        Task InstallExtension(string name);

        Task RemoveExtension();
    }

    public interface IGenericExtensionTestGrain<in T> : IGrainWithIntegerKey
    {
        Task InstallExtension(T name);

        Task RemoveExtension();
    }

    public interface IGenericGrainWithNonGenericExtension<in T> : IGrainWithIntegerKey
    {
        Task DoSomething();
    }

    public interface INoOpTestGrain : IGrainWithIntegerKey
    {
    }
}