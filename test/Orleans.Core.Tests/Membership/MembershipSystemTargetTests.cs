using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipSystemTargetTests
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task ProbeIndirectly_QueriesLocalHealthAndReportsProviderElapsedTime()
        {
            var probeTimeout = TimeSpan.FromSeconds(10);
            var elapsed = TimeSpan.FromMilliseconds(3_250);
            const int ProbeNumber = 17;
            var targetSilo = SiloAddress.FromParsableString("127.0.0.1:200@100");
            var healthStatus = CreateDistinctStallStatus();
            using var rig = CreateTestRig(healthStatus);

            var responseTask = rig.Target.ProbeIndirectly(targetSilo, probeTimeout, ProbeNumber);
            await rig.PingEntered.Task;

            Assert.False(responseTask.IsCompleted);
            Assert.Empty(rig.LocalHealthMonitor.ReceivedCalls());
            rig.TimeProvider.Advance(elapsed);
            rig.PingRelease.SetResult();

            var response = await responseTask;

            Assert.True(response.Succeeded);
            Assert.Equal(healthStatus.Score, response.IntermediaryHealthScore);
            Assert.Equal(elapsed, response.ProbeResponseTime);
            Assert.Null(response.FailureMessage);
            rig.LocalHealthMonitor.Received(1).GetLocalHealthStatus(
                Start,
                Start + elapsed,
                LocalSiloHealthCheckCategory.Local);
            rig.GrainFactory.Received(1).GetSystemTarget<IMembershipService>(
                Constants.MembershipServiceType,
                targetSilo);
            await rig.RemoteMembershipService.Received(1).Ping(ProbeNumber);
            Assert.Same(rig.Target, rig.ActivationDirectory.FindTarget(rig.Target.GrainId));
        }

        [Fact]
        public async Task ProbeIndirectly_TimesOutUsingInjectedProvider()
        {
            var probeTimeout = TimeSpan.FromSeconds(5);
            const int ProbeNumber = 23;
            var targetSilo = SiloAddress.FromParsableString("127.0.0.1:300@100");
            var healthStatus = CreateDistinctStallStatus();
            using var rig = CreateTestRig(healthStatus);

            try
            {
                var responseTask = rig.Target.ProbeIndirectly(targetSilo, probeTimeout, ProbeNumber);
                await rig.PingEntered.Task;

                Assert.Empty(rig.LocalHealthMonitor.ReceivedCalls());
                rig.TimeProvider.Advance(probeTimeout - TimeSpan.FromTicks(1));
                Assert.False(responseTask.IsCompleted);

                rig.TimeProvider.Advance(TimeSpan.FromTicks(1));
                var response = await responseTask;

                Assert.False(response.Succeeded);
                Assert.Equal(healthStatus.Score, response.IntermediaryHealthScore);
                Assert.Equal(probeTimeout, response.ProbeResponseTime);
                Assert.Contains(nameof(TimeoutException), response.FailureMessage, StringComparison.Ordinal);
                rig.LocalHealthMonitor.Received(1).GetLocalHealthStatus(
                    Start,
                    Start + probeTimeout,
                    LocalSiloHealthCheckCategory.Local);
                rig.GrainFactory.Received(1).GetSystemTarget<IMembershipService>(
                    Constants.MembershipServiceType,
                    targetSilo);
                await rig.RemoteMembershipService.Received(1).Ping(ProbeNumber);
            }
            finally
            {
                rig.PingRelease.TrySetResult();
            }
        }

        private static LocalSiloHealthStatus CreateDistinctStallStatus() => new(
            Score: 6,
            Events:
            [
                new(
                    Start,
                    LocalSiloHealthCheckKind.GarbageCollectionPause,
                    LocalSiloHealthCheckCategory.Local,
                    Source: null,
                    Score: 1,
                    Complaint: "gc pause",
                    Duration: TimeSpan.FromSeconds(1)),
                new(
                    Start,
                    LocalSiloHealthCheckKind.RuntimeStall,
                    LocalSiloHealthCheckCategory.Local,
                    Source: null,
                    Score: 2,
                    Complaint: "runtime stall",
                    Duration: TimeSpan.FromSeconds(2)),
                new(
                    Start,
                    LocalSiloHealthCheckKind.ComponentHealthCheckStall,
                    LocalSiloHealthCheckCategory.Local,
                    Source: null,
                    Score: 3,
                    Complaint: "component stall",
                    Duration: TimeSpan.FromSeconds(3)),
            ]);

        private static MembershipSystemTargetTestRig CreateTestRig(LocalSiloHealthStatus healthStatus)
        {
            var timeProvider = new FakeTimeProvider(Start);
            var localHealthMonitor = Substitute.For<ILocalSiloHealthMonitor>();
            localHealthMonitor.GetLocalHealthStatus(default, default, default).ReturnsForAnyArgs(healthStatus);
            var services = new ServiceCollection();
            services.AddSingleton(localHealthMonitor);
            services.AddMetrics();
            services.AddSingleton<OrleansInstruments>();
            services.AddSingleton<CatalogInstruments>();
            services.AddSingleton<SchedulerInstruments>();
            services.AddSingleton<GrainInstruments>();
            services.AddSingleton<MessagingInstruments>();
            services.AddSingleton<MessagingProcessingInstruments>();
            var serviceProvider = services.BuildServiceProvider();

            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(SiloAddress.FromParsableString("127.0.0.1:100@100"));
            var activationDirectory = new ActivationDirectory(serviceProvider.GetRequiredService<CatalogInstruments>());
            var runtimeClient = (InsideRuntimeClient)RuntimeHelpers.GetUninitializedObject(typeof(InsideRuntimeClient));
            typeof(InsideRuntimeClient)
                .GetField("<ServiceProvider>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(runtimeClient, serviceProvider);
            var shared = new SystemTargetShared(
                runtimeClient,
                localSiloDetails,
                NullLoggerFactory.Instance,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                activationDirectory,
                serviceProvider.GetRequiredService<SchedulerInstruments>(),
                serviceProvider.GetRequiredService<GrainInstruments>(),
                serviceProvider.GetRequiredService<MessagingInstruments>(),
                serviceProvider.GetRequiredService<MessagingProcessingInstruments>());

            var pingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pingRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var remoteMembershipService = Substitute.For<IMembershipService>();
            remoteMembershipService.Ping(Arg.Any<int>()).Returns(_ =>
            {
                pingEntered.TrySetResult();
                return pingRelease.Task;
            });
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            grainFactory
                .GetSystemTarget<IMembershipService>(Constants.MembershipServiceType, Arg.Any<SiloAddress>())
                .Returns(remoteMembershipService);
            var target = new MembershipSystemTarget(
                Substitute.For<IMembershipManager>(),
                NullLogger<MembershipSystemTarget>.Instance,
                grainFactory,
                serviceProvider.GetRequiredService<MessagingInstruments>(),
                shared,
                timeProvider);

            return new(
                serviceProvider,
                timeProvider,
                localHealthMonitor,
                grainFactory,
                remoteMembershipService,
                activationDirectory,
                target,
                pingEntered,
                pingRelease);
        }

        private sealed record MembershipSystemTargetTestRig(
            ServiceProvider ServiceProvider,
            FakeTimeProvider TimeProvider,
            ILocalSiloHealthMonitor LocalHealthMonitor,
            IInternalGrainFactory GrainFactory,
            IMembershipService RemoteMembershipService,
            ActivationDirectory ActivationDirectory,
            MembershipSystemTarget Target,
            TaskCompletionSource PingEntered,
            TaskCompletionSource PingRelease) : IDisposable
        {
            public void Dispose() => ServiceProvider.Dispose();
        }
    }
}
