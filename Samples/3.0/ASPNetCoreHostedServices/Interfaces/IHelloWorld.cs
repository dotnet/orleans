using System.Threading.Tasks;

namespace AspNetCoreHostedServices.Interfaces
{
    public interface IHelloWorld : Orleans.IGrainWithIntegerKey
    {
        Task<string> SayHello();
    }
}