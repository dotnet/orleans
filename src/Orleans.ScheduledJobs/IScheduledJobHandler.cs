using System.Threading;
using System.Threading.Tasks;

namespace Orleans.ScheduledJobs;

public interface IScheduledJobContext
{
    IScheduledJob Job { get; }

    string RunId { get; }
}

[GenerateSerializer]
internal class ScheduledJobContext : IScheduledJobContext
{
    [Id(0)]
    public IScheduledJob Job { get; }

    [Id(1)]
    public string RunId { get; }

    public ScheduledJobContext(IScheduledJob job, string runId)
    {
        Job = job;
        RunId = runId;
    }
}

public interface IScheduledJobHandler
{
    Task ExecuteJobAsync(IScheduledJobContext context, CancellationToken cancellationToken);
}
