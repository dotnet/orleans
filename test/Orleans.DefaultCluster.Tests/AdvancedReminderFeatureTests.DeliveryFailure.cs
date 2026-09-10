using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;
using Orleans.Runtime;
using Xunit;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace DefaultCluster.Tests;

public sealed partial class AdvancedReminderFeatureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task OneShot_DeliveryFailsBeforeCallback_RetainsWorkUntilRecipientRuns(int failures)
    {
        var filter = new RecipientFailureFilter(failures);
        await using var cluster = await CreateClusterAsync(retryPolicy: ReminderRetryPolicy.UntilSuccess,
            configureServices: services => services.AddSingleton<IIncomingGrainCallFilter>(filter));
        using var filterLifetime = filter;
        var grain = cluster.Client.GetGrain<IAdvancedReminderFeatureGrain>(Guid.NewGuid());
        filter.Target = grain.GetGrainId();
        var name = Guid.NewGuid().ToString("N");
        using var completion = new ReminderCompletions(name);
        await grain.RegisterOneShot(name, DateTimeOffset.UtcNow, MissedReminderAction.FireImmediately, failures: 0);
        var original = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
        var previousOccurrence = original.ScheduleId;

        for (var failure = 0; failure < failures; failure++)
        {
            filter.AllowAttempt();
            Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
            var retained = Assert.IsType<ReminderEntry>(await GetEntryAsync(cluster, grain, name));
            Assert.Equal(original.StartAt, retained.StartAt);
            Assert.NotEqual(previousOccurrence, retained.ScheduleId);
            Assert.NotEmpty(retained.JobId);
            Assert.NotEmpty(retained.JobShardId);
            previousOccurrence = retained.ScheduleId;
        }

        filter.AllowAttempt();
        Assert.Equal(ActivityStatusCode.Ok, await completion.NextAsync());
        Assert.Equal(failures + 1, filter.Attempts);
        Assert.Equal(1, await grain.GetTickCount());
        Assert.Null(await GetEntryAsync(cluster, grain, name));
        Assert.Equal(failures + 1, completion.JobIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(completion.DequeueCounts, count => Assert.Equal(1, count));
    }

    private sealed class RecipientFailureFilter(int failures) : IIncomingGrainCallFilter, IDisposable
    {
        private readonly Channel<bool> _attempts = Channel.CreateUnbounded<bool>();
        public GrainId Target { get; set; }
        public int Attempts { get; private set; }

        public void AllowAttempt() => _attempts.Writer.TryWrite(true);

        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.TargetId == Target
                && context.InterfaceMethod.DeclaringType == typeof(Orleans.AdvancedReminders.IRemindable))
            {
                // Hold each delivery until the test has inspected the previous result.
                await _attempts.Reader.ReadAsync(TestContext.Current.CancellationToken);
                Attempts++;
                if (Attempts <= failures)
                {
                    throw new InvalidOperationException("Injected delivery failure before recipient callback.");
                }
            }

            await context.Invoke();
        }

        public void Dispose() => _attempts.Writer.TryComplete();
    }
}
