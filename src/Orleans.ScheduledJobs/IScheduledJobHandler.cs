using System.Threading;
using System.Threading.Tasks;

namespace Orleans.ScheduledJobs;

public interface IScheduledJobContext
{
    IScheduledJob Job { get; }

    string RunId { get; }

    int DequeueCount { get; }
}

[GenerateSerializer]
internal class ScheduledJobContext : IScheduledJobContext
{
    [Id(0)]
    public IScheduledJob Job { get; }

    [Id(1)]
    public string RunId { get; }

    [Id(2)]
    public int DequeueCount { get; }

    public ScheduledJobContext(IScheduledJob job, string runId, int retryCount)
    {
        Job = job;
        RunId = runId;
        DequeueCount = retryCount;
    }
}

public interface IScheduledJobHandler
{
    Task ExecuteJobAsync(IScheduledJobContext context, CancellationToken cancellationToken);
}
