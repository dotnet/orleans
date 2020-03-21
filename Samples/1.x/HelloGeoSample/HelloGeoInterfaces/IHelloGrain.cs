using System.Threading.Tasks;

namespace HelloGeoInterfaces
{
    /// <summary>
    /// Grain interface for the hello grains
    /// </summary>
    public interface IHelloGrain : Orleans.IGrainWithStringKey
    {
        Task<string> Ping();
    }
}