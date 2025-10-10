using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

public interface IScheduledJobReceiver : IGrainExtension
{
    Task ReceiveScheduledJobAsync(IScheduledJob job);
}
