using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.Forwarding
{
    /// <summary>
    /// Tests for silo shutdown scenarios including request forwarding, timer handling, and stuck activations.
    /// </summary>
    public class ShutdownSiloTests : TestClusterPerTest
    {
        public const int NumberOfSilos = 2;

        public static readonly TimeSpan DeactivationTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan PendingRequestTimerDueTime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan StuckActivationTimerDueTime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan TimerReadinessTimeout = TimeSpan.FromMinutes(1);

        internal class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .Configure<GrainCollectionOptions>(options =>
                    {
                        options.DeactivationTimeout = DeactivationTimeout;
                    })
                    .Configure<SiloMessagingOptions>(options =>
                    {
                        options.MaxRequestProcessingTime = DeactivationTimeout;
                    })
                    .Configure<HostOptions>(options =>
                    {
                        options.ShutdownTimeout = ShutdownTimeout;
                    })
                    .UseAzureStorageClustering(options => options.TableServiceClient = GetTableServiceClient())
                    .ConfigureServices(services => services.AddSingleton<PlacementStrategy, ActivationCountBasedPlacement>())
                    .Configure<ClusterMembershipOptions>(options =>
                    {
                        options.NumMissedProbesLimit = 1;
                        options.NumVotesForDeathDeclaration = 1;
                    });
            }

            private static TableServiceClient GetTableServiceClient()
            {
                return TestDefaultConfiguration.UseAadAuthentication
                    ? new(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential)
                    : new(TestDefaultConfiguration.DataConnectionString);
            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = NumberOfSilos;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public ShutdownSiloTests()
        {
            this.EnsurePreconditionsMet();
        }

        [Fact(Skip = "https://github.com/dotnet/orleans/issues/6423"), TestCategory("Forward"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_ForwardPendingRequest()
        {
            var grain = await GetLongRunningTaskGrainOnSecondary<bool>();

            var tasks = new List<Task<string>>();
            for (int i = 0; i < 100; i++)
            {
                tasks.Add(grain.GetRuntimeInstanceIdWithDelay(TimeSpan.FromMilliseconds(50)));
            }

            // Shutdown the silo where the grain is
            await Task.Delay(500);
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());

            var results = await Task.WhenAll(tasks);
            Assert.Equal(results[99], HostedCluster.Primary.SiloAddress.ToString());
        }

        [SkippableFact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_PendingRequestTimers()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var timerCreated = new TaskCompletionSource<GrainTimerEvents.Created>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = SubscribeToTimerEvent(grain.GetGrainId(), timerCreated);

            var promise = grain.StartAndWaitTimerTick(PendingRequestTimerDueTime);

            await timerCreated.Task.WaitAsync(TimerReadinessTimeout);
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());

            await promise;
        }

        [SkippableFact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_StuckTimers()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var timerStarted = new TaskCompletionSource<GrainTimerEvents.TickStart>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timerStopped = new TaskCompletionSource<GrainTimerEvents.TickStop>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var startedSubscription = SubscribeToTimerEvent(grain.GetGrainId(), timerStarted);
            using var stoppedSubscription = SubscribeToTimerEvent(grain.GetGrainId(), timerStopped);

            await grain.StartStuckTimer(TimeSpan.Zero);

            await timerStarted.Task.WaitAsync(TimerReadinessTimeout);
            AssertTimerTickDidNotCompleteSuccessfully(timerStopped.Task);
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());
            AssertTimerTickDidNotCompleteSuccessfully(timerStopped.Task);
        }

        [SkippableFact, TestCategory("GracefulShutdown"), TestCategory("Functional")]
        public async Task SiloGracefulShutdown_StuckActivation()
        {
            var grain = await GetTimerRequestGrainOnSecondary();

            var timerCreated = new TaskCompletionSource<GrainTimerEvents.Created>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = SubscribeToTimerEvent(grain.GetGrainId(), timerCreated);

            var promise = grain.StartAndWaitTimerTick(StuckActivationTimerDueTime);

            await timerCreated.Task.WaitAsync(TimerReadinessTimeout);
            AssertTaskDidNotCompleteSuccessfully(promise);
            await HostedCluster.StopSiloAsync(HostedCluster.SecondarySilos.First());
            AssertTaskDidNotCompleteSuccessfully(promise);
        }

        private async Task<ILongRunningTaskGrain<T>> GetLongRunningTaskGrainOnSecondary<T>()
        {
            while (true)
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, HostedCluster.SecondarySilos[0].SiloAddress);
                var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<T>>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.SecondarySilos[0].SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
        }

        private async Task<ITimerRequestGrain> GetTimerRequestGrainOnSecondary()
        {
            var i = 0;
            while (true)
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, HostedCluster.SecondarySilos[0].SiloAddress);
                var grain = GrainFactory.GetGrain<ITimerRequestGrain>(i++);
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.SecondarySilos[0].SiloAddress.Endpoint.ToString()))
                {
                    return grain;
                }
            }
        }

        private static IDisposable SubscribeToTimerEvent<TEvent>(GrainId grainId, TaskCompletionSource<TEvent> completion)
            where TEvent : GrainTimerEvents.TimerEvent
        {
            return GrainTimerEvents.AllEvents.Subscribe(new TimerEventObserver(evt =>
            {
                if (evt is TEvent typedEvent && typedEvent.GrainContext.GrainId.Equals(grainId))
                {
                    completion.TrySetResult(typedEvent);
                }
            }));
        }

        private static void AssertTimerTickDidNotCompleteSuccessfully(Task<GrainTimerEvents.TickStop> timerStopped)
        {
            Assert.False(
                timerStopped.IsCompletedSuccessfully && timerStopped.Result.Exception is null,
                "The timer tick completed successfully before shutdown completed.");
        }

        private static void AssertTaskDidNotCompleteSuccessfully(Task task)
        {
            if (task.IsFaulted)
            {
                _ = task.Exception;
            }

            Assert.False(
                task.IsCompletedSuccessfully,
                "The timer request completed successfully before shutdown completed.");
        }

        private sealed class TimerEventObserver : IObserver<GrainTimerEvents.TimerEvent>
        {
            private readonly Action<GrainTimerEvents.TimerEvent> _onNext;

            public TimerEventObserver(Action<GrainTimerEvents.TimerEvent> onNext)
            {
                _onNext = onNext;
            }

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(GrainTimerEvents.TimerEvent value) => _onNext(value);
        }
    }
}
