using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces
{
    [Factory(FactoryAttribute.FactoryTypes.ClientObject)]
    public interface IClientAddressableTestClientObject : IGrain
    {
        Task<string> OnHappyPath(string message);
        Task OnSadPath(string message);
        Task<int> OnSerialStress(int n);
        Task<int> OnParallelStress(int n);
    }
}
