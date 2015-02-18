using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces
{
    public interface IExtensionTestGrain : IGrain
    {
        Task InstallExtension(string name);

        Task RemoveExtension();
    }

    public interface IGenericExtensionTestGrain<in T> : IGrain
    {
        Task InstallExtension(T name);

        Task RemoveExtension();
    }
}