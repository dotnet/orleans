using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class DurableJobManagerProbe
{
    private readonly ConcurrentDictionary<(string JobName, GrainId Target), int> _attempts = [];
    private readonly ConcurrentDictionary<(string JobName, GrainId Target), int> _successes = [];
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _postScheduleFailures = new(StringComparer.Ordinal);

    public void FailNext(string jobName) =>
        _failures.AddOrUpdate(jobName, 1, static (_, count) => count + 1);

    public void FailAfterNext(string jobName) =>
        _postScheduleFailures.AddOrUpdate(jobName, 1, static (_, count) => count + 1);

    public int GetAttemptCount(string jobName, GrainId target) =>
        _attempts.TryGetValue((jobName, target), out var count) ? count : 0;

    public int GetSuccessCount(string jobName, GrainId target) =>
        _successes.TryGetValue((jobName, target), out var count) ? count : 0;

    internal void OnAttempt(ScheduleJobRequest request) =>
        _attempts.AddOrUpdate((request.JobName, request.Target), 1, static (_, count) => count + 1);

    internal void OnSuccess(ScheduleJobRequest request) =>
        _successes.AddOrUpdate((request.JobName, request.Target), 1, static (_, count) => count + 1);

    internal bool ShouldFail(string jobName)
        => TryConsumeFailure(_failures, jobName);

    internal bool ShouldFailAfterSchedule(string jobName)
        => TryConsumeFailure(_postScheduleFailures, jobName);

    private static bool TryConsumeFailure(
        ConcurrentDictionary<string, int> failures,
        string jobName)
    {
        while (failures.TryGetValue(jobName, out var remaining) && remaining > 0)
        {
            if (failures.TryUpdate(jobName, remaining - 1, remaining))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class ControlledDurableJobManager(
    ILocalDurableJobManager inner,
    DurableJobManagerProbe probe) : ILocalDurableJobManager
{
    public async Task<DurableJob> ScheduleJobAsync(
        ScheduleJobRequest request,
        CancellationToken cancellationToken)
    {
        probe.OnAttempt(request);
        if (probe.ShouldFail(request.JobName))
        {
            throw new IOException($"Injected durable job scheduling failure for '{request.JobName}'.");
        }

        var result = await inner.ScheduleJobAsync(request, cancellationToken);
        probe.OnSuccess(request);
        if (probe.ShouldFailAfterSchedule(request.JobName))
        {
            throw new IOException(
                $"Injected durable job scheduling response failure for '{request.JobName}'.");
        }

        return result;
    }

    public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
        inner.CancelAsync(job, cancellationToken);

    public static void Decorate(IServiceCollection services, DurableJobManagerProbe probe)
    {
        var descriptor = services.Last(service => service.ServiceType == typeof(ILocalDurableJobManager));
        var factory = descriptor.ImplementationFactory
            ?? throw new InvalidOperationException("The durable job manager registration must use an implementation factory.");
        services.Remove(descriptor);
        services.AddSingleton<ILocalDurableJobManager>(
            serviceProvider => new ControlledDurableJobManager(
                (ILocalDurableJobManager)factory(serviceProvider),
                probe));
    }
}
