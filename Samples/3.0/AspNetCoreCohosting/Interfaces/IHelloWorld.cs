using System.Threading.Tasks;

namespace AspNetCoreCohosting.Interfaces
{
    public interface IHelloWorld : Orleans.IGrainWithIntegerKey
    {
        Task<string> SayHello();
    }
}