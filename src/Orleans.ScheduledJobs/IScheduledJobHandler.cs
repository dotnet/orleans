using System.Threading;
using System.Threading.Tasks;

namespace Orleans.ScheduledJobs;

public interface IScheduledJobContext
{
    IScheduledJob Job { get; }
}

public interface IScheduledJobHandler
{
    Task ExecuteJobAsync(IScheduledJobContext context, CancellationToken cancellationToken);
}
