using System.Threading.Tasks;

namespace HelloWorld.Interfaces
{
    /// <summary>
    /// Orleans grain communication interface IHello
    /// </summary>
    public interface IHelloGrain : Orleans.IGrainWithIntegerKey
    {
        Task<string> SayHello(string greeting);
    }
}
