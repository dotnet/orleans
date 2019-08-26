using System.Threading.Tasks;

namespace Orleans.Networking.Shared
{
    internal delegate Task ConnectionDelegate(ConnectionContext connection);
}
