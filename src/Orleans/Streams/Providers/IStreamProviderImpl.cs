using System.Threading.Tasks;
using Orleans.Providers;

namespace Orleans.Streams
{
    public interface IStreamProviderImpl : IStreamProvider, IProvider
    {
        Task Start();
    }
}
