using System.Threading.Tasks;

namespace Orleans
{
    public interface ILifecycleObserver
    {
        Task OnStart();
        Task OnStop();
    }
}
