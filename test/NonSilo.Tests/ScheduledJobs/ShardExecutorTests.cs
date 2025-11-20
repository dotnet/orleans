using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Runtime.Messaging;
using Xunit;

namespace NonSilo.Tests.ScheduledJobs;

[TestCategory("DurableJobs")]
public class ShardExecutorTests
{
    [Fact]
    public async Task RunShardAsync_WhenNotOverloaded_ProcessesJobsWithoutDelay()
    {
        var options = CreateOptions(maxConcurrentJobs: 10);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(3);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify all jobs were processed and removed from the shard
        Assert.Equal(3, completedJobs.Count);
        Assert.Contains("job-0", completedJobs);
        Assert.Contains("job-1", completedJobs);
        Assert.Contains("job-2", completedJobs);

        await shard.Received(3).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WhenOverloaded_PausesAndRetriesWithBackoffDelay()
    {
        var options = CreateOptions(maxConcurrentJobs: 10, overloadBackoffDelay: TimeSpan.FromMilliseconds(50));
        var overloadDetector = Substitute.For<IOverloadDetector>();
        var jobs = CreateJobs(2);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        // Simulate system being overloaded initially, then clearing after 3 checks
        var checkCount = 0;
        overloadDetector.IsOverloaded.Returns(_ =>
        {
            checkCount++;
            return checkCount <= 3;
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Jobs should complete successfully after overload clears, and detector should be checked multiple times
        Assert.Equal(2, completedJobs.Count);
        Assert.True(checkCount > 3, $"Expected multiple overload checks, got {checkCount}");
    }

    [Fact]
    public async Task RunShardAsync_WhenOverloadTransitionsDuringProcessing_HandlesStateChanges()
    {
        var options = CreateOptions(maxConcurrentJobs: 10, overloadBackoffDelay: TimeSpan.FromMilliseconds(10));
        var overloadDetector = Substitute.For<IOverloadDetector>();
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        // Jobs arrive gradually to allow overload state to toggle during processing
        var shard = CreateJobShardWithDelayedYield(5, TimeSpan.FromMilliseconds(10));

        // Alternate overload state with each check: overloaded, not overloaded, overloaded...
        var checkCount = 0;
        overloadDetector.IsOverloaded.Returns(_ =>
        {
            checkCount++;
            return checkCount % 2 == 1;
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // All jobs should complete despite the toggling overload state
        Assert.Equal(5, completedJobs.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Contains($"job-{i}", completedJobs);
        }
    }

    [Fact]
    public async Task RunShardAsync_RespectsMaxConcurrentJobsPerSilo_WhileCheckingOverload()
    {
        var maxConcurrent = 3;
        var options = CreateOptions(maxConcurrentJobs: maxConcurrent);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var currentConcurrent = 0;
        var maxObservedConcurrent = 0;
        var concurrentLock = new object();

        var jobs = CreateJobs(10);
        var shard = CreateJobShard(jobs);
        
        // Track the maximum concurrent job execution count
        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, async () =>
        {
            lock (concurrentLock)
            {
                currentConcurrent++;
                if (currentConcurrent > maxObservedConcurrent)
                {
                    maxObservedConcurrent = currentConcurrent;
                }
            }

            await Task.Delay(50);

            lock (concurrentLock)
            {
                currentConcurrent--;
            }
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify concurrency limit was respected while jobs executed in parallel
        Assert.True(maxObservedConcurrent <= maxConcurrent, 
            $"Max concurrent jobs was {maxObservedConcurrent}, but limit was {maxConcurrent}");
        Assert.True(maxObservedConcurrent > 1, 
            "Expected some concurrent execution");
    }

    [Fact]
    public async Task RunShardAsync_WhenCancelledDuringOverloadBackoff_CancelsCleanly()
    {
        var options = CreateOptions(maxConcurrentJobs: 10, overloadBackoffDelay: TimeSpan.FromSeconds(10));
        var overloadDetector = CreateOverloadDetector(isOverloaded: true);
        var jobs = CreateJobs(5);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        // Cancel shortly after starting, while executor is waiting for overload to clear
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await executor.RunShardAsync(shard, cts.Token);
        });

        // No jobs should have executed since cancellation occurred during backoff wait
        Assert.Empty(completedJobs);
    }

    [Fact]
    public async Task RunShardAsync_WhenJobFailsDuringOverload_ContinuesOverloadChecking()
    {
        var options = CreateOptions(
            maxConcurrentJobs: 10,
            overloadBackoffDelay: TimeSpan.FromMilliseconds(10),
            shouldRetry: (context, ex) => DateTimeOffset.UtcNow.AddSeconds(1)
        );
        var overloadDetector = Substitute.For<IOverloadDetector>();
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var jobs = CreateJobs(3);
        var shard = CreateJobShard(jobs);

        var completedJobs = new List<string>();
        var failedJobs = new List<string>();
        var jobExecutionCount = 0;

        // Periodically report overload to test interaction with job failures
        var checkCount = 0;
        overloadDetector.IsOverloaded.Returns(_ =>
        {
            checkCount++;
            return checkCount % 3 == 1;
        });

        // First job fails, remaining jobs succeed
        ConfigureGrainFactoryWithSelectiveFailures(grainFactory, completedJobs, failedJobs, ref jobExecutionCount);

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Failed job should be scheduled for retry, successful jobs should be removed
        Assert.Equal(2, completedJobs.Count);
        Assert.Single(failedJobs);
        
        await shard.Received(1).RetryJobLaterAsync(
            Arg.Is<IDurableJobContext>(ctx => ctx.Job.Id == "job-0"),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        
        await shard.Received(2).RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunShardAsync_WaitsForShardStartTime_BeforeProcessing()
    {
        var options = CreateOptions(maxConcurrentJobs: 10);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(1);
        var futureStartTime = DateTimeOffset.UtcNow.AddMilliseconds(200);
        var shard = CreateJobShard(jobs, startTime: futureStartTime);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        ConfigureGrainFactoryToTrackCompletions(grainFactory, completedJobs);

        var startTime = DateTimeOffset.UtcNow;

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify executor waited for shard start time before processing
        var elapsed = DateTimeOffset.UtcNow - startTime;
        Assert.True(elapsed.TotalMilliseconds >= 150, 
            $"Expected to wait for shard start time, but elapsed only {elapsed.TotalMilliseconds}ms");
        Assert.Single(completedJobs);
    }

    [Fact]
    public async Task RunShardAsync_WaitsForAllJobsToComplete_BeforeReturning()
    {
        var options = CreateOptions(maxConcurrentJobs: 5);
        var overloadDetector = CreateOverloadDetector(isOverloaded: false);
        var jobs = CreateJobs(5);
        var shard = CreateJobShard(jobs);
        var grainFactory = CreateGrainFactory();
        var executor = new ShardExecutor(grainFactory, options, overloadDetector, NullLogger<ShardExecutor>.Instance);

        var completedJobs = new List<string>();
        var runningJobs = 0;
        var lockObj = new object();

        // Simulate slow job execution to ensure some run concurrently
        ConfigureGrainFactoryWithSlowJobExecution(grainFactory, async () =>
        {
            lock (lockObj) { runningJobs++; }
            await Task.Delay(100);
            lock (lockObj) 
            { 
                runningJobs--;
                completedJobs.Add($"job-{completedJobs.Count}");
            }
        });

        await executor.RunShardAsync(shard, CancellationToken.None);

        // Verify all jobs completed before RunShardAsync returned
        Assert.Equal(0, runningJobs);
        Assert.Equal(5, completedJobs.Count);
    }

    // Helper methods

    private static IOptions<DurableJobsOptions> CreateOptions(
        int maxConcurrentJobs = 10,
        TimeSpan? overloadBackoffDelay = null,
        Func<IDurableJobContext, Exception, DateTimeOffset?> shouldRetry = null)
    {
        var options = new DurableJobsOptions
        {
            MaxConcurrentJobsPerSilo = maxConcurrentJobs,
            OverloadBackoffDelay = overloadBackoffDelay ?? TimeSpan.FromMilliseconds(100),
            ShouldRetry = shouldRetry ?? ((_, _) => null) // Default: no retry
        };
        return Options.Create(options);
    }

    private static IOverloadDetector CreateOverloadDetector(bool isOverloaded)
    {
        var detector = Substitute.For<IOverloadDetector>();
        detector.IsOverloaded.Returns(isOverloaded);
        return detector;
    }

    private static List<DurableJob> CreateJobs(int count, DateTimeOffset? dueTime = null)
    {
        var jobs = new List<DurableJob>();
        var baseDueTime = dueTime ?? DateTimeOffset.UtcNow.AddMilliseconds(-100);
        
        for (int i = 0; i < count; i++)
        {
            jobs.Add(new DurableJob
            {
                Id = $"job-{i}",
                Name = $"job-{i}",
                DueTime = baseDueTime.AddMilliseconds(i * 10),
                TargetGrainId = GrainId.Create("test", $"grain-{i}"),
                ShardId = "shard-1",
                Metadata = null
            });
        }
        
        return jobs;
    }

    private static IJobShard CreateJobShard(
        List<DurableJob> jobs, 
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null)
    {
        var shard = Substitute.For<IJobShard>();
        shard.Id.Returns("shard-1");
        shard.StartTime.Returns(startTime ?? DateTimeOffset.UtcNow.AddMinutes(-10));
        shard.EndTime.Returns(endTime ?? DateTimeOffset.UtcNow.AddMinutes(10));

        shard.ConsumeDurableJobsAsync().Returns(callInfo => CreateJobContexts(jobs));

        shard.RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        return shard;
    }

    private static IJobShard CreateJobShardWithDelayedYield(int jobCount, TimeSpan yieldDelay)
    {
        var jobs = CreateJobs(jobCount);
        var shard = Substitute.For<IJobShard>();
        shard.Id.Returns("shard-1");
        shard.StartTime.Returns(DateTimeOffset.UtcNow.AddMinutes(-10));
        shard.EndTime.Returns(DateTimeOffset.UtcNow.AddMinutes(10));

        shard.ConsumeDurableJobsAsync().Returns(callInfo => CreateJobContextsWithDelay(jobs, yieldDelay));

        shard.RemoveJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        return shard;
    }

    private static async IAsyncEnumerable<IDurableJobContext> CreateJobContexts(List<DurableJob> jobs)
    {
        foreach (var job in jobs)
        {
            var context = Substitute.For<IDurableJobContext>();
            context.Job.Returns(job);
            context.RunId.Returns(Guid.NewGuid().ToString());
            context.DequeueCount.Returns(1);
            yield return context;
        }
        
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IDurableJobContext> CreateJobContextsWithDelay(
        List<DurableJob> jobs, 
        TimeSpan delay)
    {
        foreach (var job in jobs)
        {
            await Task.Delay(delay);
            
            var context = Substitute.For<IDurableJobContext>();
            context.Job.Returns(job);
            context.RunId.Returns(Guid.NewGuid().ToString());
            context.DequeueCount.Returns(1);
            yield return context;
        }
    }

    private static IInternalGrainFactory CreateGrainFactory()
    {
        var factory = Substitute.For<IInternalGrainFactory>();
        
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.DeliverDurableJobAsync(Arg.Any<IDurableJobContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        
        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        
        return factory;
    }

    private static void ConfigureGrainFactoryToTrackCompletions(
        IInternalGrainFactory factory, 
        List<string> completedJobs)
    {
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.DeliverDurableJobAsync(Arg.Any<IDurableJobContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var context = callInfo.ArgAt<IDurableJobContext>(0);
                lock (completedJobs)
                {
                    completedJobs.Add(context.Job.Id);
                }
                return Task.CompletedTask;
            });
        
        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
    }

    private static void ConfigureGrainFactoryWithSlowJobExecution(
        IInternalGrainFactory factory,
        Func<Task> executionAction)
    {
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.DeliverDurableJobAsync(Arg.Any<IDurableJobContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => executionAction());
        
        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
    }

    private static void ConfigureGrainFactoryWithSelectiveFailures(
        IInternalGrainFactory factory,
        List<string> completedJobs,
        List<string> failedJobs,
        ref int jobExecutionCount)
    {
        var executionCount = jobExecutionCount;
        
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.DeliverDurableJobAsync(Arg.Any<IDurableJobContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var context = callInfo.ArgAt<IDurableJobContext>(0);
                var currentExecution = Interlocked.Increment(ref executionCount);
                
                // First job fails
                if (currentExecution == 1)
                {
                    lock (failedJobs)
                    {
                        failedJobs.Add(context.Job.Id);
                    }
                    throw new InvalidOperationException("Simulated job failure");
                }
                
                // Other jobs succeed
                lock (completedJobs)
                {
                    completedJobs.Add(context.Job.Id);
                }
                return Task.CompletedTask;
            });
        
        factory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        
        jobExecutionCount = executionCount;
    }
}
