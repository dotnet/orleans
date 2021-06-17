using System.Threading.Tasks;

namespace HelloWorldInterfaces
{
    public interface IHelloGrain : Orleans.IGrainWithIntegerKey
    {
        Task<string> SayHello(string greeting);
    }
}
