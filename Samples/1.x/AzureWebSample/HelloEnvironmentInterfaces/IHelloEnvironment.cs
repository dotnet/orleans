using System.Threading.Tasks;

namespace HelloEnvironmentInterfaces
{
    /// <summary>
    /// Orleans grain communication interface IHello
    /// </summary>
    public interface IHelloEnvironment : Orleans.IGrainWithIntegerKey
    {
        Task<string> RequestDetails();
    }
}
