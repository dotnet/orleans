using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface ITestExtension : IGrainWithIntegerKey, IGrainExtension
    {
        Task<string> CheckExtension_1();

        Task<string> CheckExtension_2();
    }

    public interface IGenericTestExtension<T> : IGrainWithIntegerKey, IGrainExtension
    {
        Task<T> CheckExtension_1();

        Task<string> CheckExtension_2();
    }
}