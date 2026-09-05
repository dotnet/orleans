using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Xunit;

namespace UnitTests.ClusterServices;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("GrainDirectory")]
[TestCategory("BVT")]
public sealed class GrainDirectoryTransitionTests
{
    [Theory]
    [InlineData("LogDebugRecoveringActivations")]
    [InlineData("LogDebugCompletedTransferringEntries")]
    public async Task FailedAcquisitionKeepsRangeBlockedAndReportsFatalError(string failurePoint)
    {
        await using var fixture = new Fixture();
        await fixture.ApplyViewAsync(1, active: false);
        fixture.Logger.FailurePoint = failurePoint;

        await fixture.ApplyViewAsync(3, active: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(fixture.WaitForRangeAsync);
        Assert.Same(fixture.Logger.Failure, Assert.Single(fixture.FatalErrors));
    }

    [Fact]
    public async Task FailedReleaseAfterDrainKeepsRangeBlockedAndReportsFatalError()
    {
        await using var fixture = new Fixture();
        await fixture.ApplyViewAsync(1, active: true);
        await fixture.WaitForRangeAsync();
        fixture.Logger.FailurePoint = "LogDebugEncounteredNonContiguousUpdate";

        await fixture.ApplyViewAsync(3, active: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(fixture.WaitForRangeAsync);
        Assert.Same(fixture.Logger.Failure, Assert.Single(fixture.FatalErrors));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SiloAddress _silo = SiloAddress.FromParsableString("127.0.0.1:11111@123");
        private readonly Channel<ClusterMembershipSnapshot> _updates = Channel.CreateUnbounded<ClusterMembershipSnapshot>();
        private readonly ServiceProvider _services;
        private readonly ActivationDirectory _activations;
        private readonly DirectoryMembershipService _membership;
        private readonly GrainDirectoryPartition _partition;
        private ClusterMembershipSnapshot _snapshot = ClusterMembershipSnapshot.Default;

        public FailureLogger Logger { get; } = new();
        public ConcurrentQueue<Exception> FatalErrors { get; } = new();

        public Fixture()
        {
            _services = new ServiceCollection()
                .AddMetrics()
                .AddSingleton<OrleansInstruments>()
                .AddSingleton<SchedulerInstruments>()
                .AddSingleton<CatalogInstruments>()
                .AddSingleton<DirectoryInstruments>()
                .AddSingleton<GrainInstruments>()
                .AddSingleton<MessagingInstruments>()
                .AddSingleton<MessagingProcessingInstruments>()
                .BuildServiceProvider();
            _activations = new(_services.GetRequiredService<CatalogInstruments>());

            var clusterMembership = Substitute.For<IClusterMembershipService>();
            clusterMembership.CurrentSnapshot.Returns(_ => _snapshot);
            clusterMembership.MembershipUpdates.Returns(_updates.Reader.ReadAllAsync());
            var grainFactory = Substitute.For<IInternalGrainFactory>();
            _membership = new(
                clusterMembership,
                grainFactory,
                NullLogger<DirectoryMembershipService>.Instance,
                1,
                DirectoryMembershipSnapshot.DefaultGetRingBoundaries);

            var siloDetails = Substitute.For<ILocalSiloDetails>();
            siloDetails.SiloAddress.Returns(_silo);
            var loggerFactory = Substitute.For<ILoggerFactory>();
            loggerFactory.CreateLogger(Arg.Any<string>()).Returns(call =>
                call.Arg<string>() == typeof(GrainDirectoryPartition).FullName ? Logger : NullLogger.Instance);
            var shared = new SystemTargetShared(
                runtimeClient: null!,
                localSiloDetails: siloDetails,
                loggerFactory,
                Options.Create(new SchedulingOptions()),
                grainReferenceActivator: null!,
                timerRegistry: null!,
                _activations,
                _services.GetRequiredService<SchedulerInstruments>(),
                _services.GetRequiredService<GrainInstruments>(),
                _services.GetRequiredService<MessagingInstruments>(),
                _services.GetRequiredService<MessagingProcessingInstruments>());
            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            fatalErrorHandler.When(handler => handler.OnFatalException(
                Arg.Any<object>(), Arg.Any<string>(), Arg.Any<Exception>()))
                .Do(call => FatalErrors.Enqueue(call.Arg<Exception>()));
            _ = new DistributedGrainDirectory(
                _membership,
                fatalErrorHandler,
                NullLogger<DistributedGrainDirectory>.Instance,
                _services,
                grainFactory,
                _services.GetRequiredService<DirectoryInstruments>(),
                Options.Create(new GrainDirectoryOptions()),
                Options.Create(new ClusterMembershipOptions()),
                TimeProvider.System,
                shared);
            _partition = Assert.IsType<GrainDirectoryPartition>(
                _activations.FindTarget(GrainDirectoryPartition.CreateGrainId(_silo, 0).GrainId));
            grainFactory.GetSystemTarget<IGrainDirectoryPartition>(Arg.Any<GrainId>()).Returns(_partition);
        }

        public async Task ApplyViewAsync(long version, bool active)
        {
            var members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty;
            if (active)
            {
                members = members.Add(_silo, new(_silo, SiloStatus.Active, "local"));
            }

            _snapshot = new(members, new(version));
            Assert.True(_updates.Writer.TryWrite(_snapshot));
            var view = await _membership.RefreshViewAsync(_snapshot.Version, TestContext.Current.CancellationToken);
            await _partition.ProcessMembershipUpdateAsync(view);
        }

        public Task WaitForRangeAsync() => _partition.QueueTask(async () =>
            await ((IGrainDirectoryTestHooks)_partition).WaitForMembershipVersionAsync(
                _snapshot.Version, TestContext.Current.CancellationToken));

        public async ValueTask DisposeAsync()
        {
            await _membership.DisposeAsync();
            await ((IAsyncDisposable)_activations).DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class FailureLogger : ILogger
    {
        public string? FailurePoint { get; set; }
        public Exception Failure { get; } = new InvalidOperationException("Injected range-transition failure.");

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == FailurePoint)
            {
                throw Failure;
            }
        }
    }
}
