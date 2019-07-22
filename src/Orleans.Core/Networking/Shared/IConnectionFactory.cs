using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Networking.Shared
{
    internal interface IConnectionFactory
    {
        ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken);
    }
}
