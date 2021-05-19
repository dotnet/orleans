using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NonSilo.Tests.Utilities;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipTableCleanupAgentTests
    {
        private readonly ITestOutputHelper output;
        private readonly LoggerFactory loggerFactory;

        public MembershipTableCleanupAgentTests(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerFactory = new LoggerFactory(new[] { new XunitLoggerProvider(this.output) });
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_Enabled_BasicScenario()
        {
            await this.BasicScenario(enabled: true);
        }

        [Fact]
        public async Task MembershipTableCleanupAgent_Disabled_BasicScenario()
        {
            await this.BasicScenario(enabled: false);
        }

        private async Task BasicScenario(bool enabled)
        {
            var options = new ClusterMembershipOptions { DefunctSiloCleanupPeriod = enabled ? new TimeSpan?(TimeSpan.FromMinutes(90)) : null, DefunctSiloExpiration = TimeSpan.FromDays(1) };
            var timers = new List<DelegateAsyncTimer>();
            var timerCalls = new ConcurrentQueue<(TimeSpan? DelayOverride, TaskCompletionSource<bool> Completion)>();
            var timerFactory = new DelegateAsyncTimerFactory(
                (period, name) =>
                {
                    Assert.Equal(options.DefunctSiloCleanupPeriod.Value, period);
                    var t = new DelegateAsyncTimer(
                        overridePeriod =>
                        {
                            var task = new TaskCompletionSource<bool>();
                            timerCalls.Enqueue((overridePeriod, task));
                            return task.Task;
                        });
                    timers.Add(t);
                    return t;
                });

            var table = new InMemoryMembershipTable();
            var cleanupAgent = new MembershipTableCleanupAgent(
                Options.Create(options),
                table,
                this.loggerFactory.CreateLogger<MembershipTableCleanupAgent>(),
                timerFactory);
            var lifecycle = new SiloLifecycleSubject(this.loggerFactory.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)cleanupAgent).Participate(lifecycle);

            await lifecycle.OnStart();
            Assert.DoesNotContain(table.Calls, c => c.Method.Equals(nameof(IMembershipTable.CleanupDefunctSiloEntries)));

            if (enabled) await Until(() => timerCalls.Count > 0);

            Assert.Equal(enabled, timerCalls.TryDequeue(out var timer));
            timer.Completion?.TrySetResult(true);

            var stopped = lifecycle.OnStop();
            if (enabled) await Until(() => timerCalls.Count > 0);
            while (timerCalls.TryDequeue(out timer)) timer.Completion.TrySetResult(false);
            if (enabled)
            {
                Assert.Contains(table.Calls, c => c.Method.Equals(nameof(IMembershipTable.CleanupDefunctSiloEntries)));
            }
            else
            {
                Assert.DoesNotContain(table.Calls, c => c.Method.Equals(nameof(IMembershipTable.CleanupDefunctSiloEntries)));
            }

            while (!stopped.IsCompleted)
            {
                while (timerCalls.TryDequeue(out var call)) call.Completion.TrySetResult(false);
                await Task.Delay(15);
            }

            await stopped;
        }

        private static async Task Until(Func<bool> condition)
        {
            var maxTimeout = 40_000;
            while (!condition() && (maxTimeout -= 10) > 0) await Task.Delay(10);
            Assert.True(maxTimeout > 0);
        }
    }
}
