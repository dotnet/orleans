using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal interface IAsyncRingRangeListener
    {
        Task RangeChangeNotification(IRingRange old, IRingRange now);
    }
}