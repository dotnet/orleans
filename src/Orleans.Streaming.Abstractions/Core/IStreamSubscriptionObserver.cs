using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionObserver
    {
        Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory);
    }
}
