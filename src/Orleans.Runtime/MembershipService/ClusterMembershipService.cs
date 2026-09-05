using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime
{
    internal partial class ClusterMembershipService : IClusterMembershipService, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> updates;
        private readonly IMembershipManager membershipManager;
        private readonly ILogger<ClusterMembershipService> log;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private ClusterMembershipSnapshot snapshot;
        private MembershipTableSnapshot tableSnapshot;

        public ClusterMembershipService(
            IMembershipManager membershipManager,
            ILogger<ClusterMembershipService> log,
            IFatalErrorHandler fatalErrorHandler)
        {
            this.tableSnapshot = membershipManager.CurrentSnapshot;
            this.snapshot = this.tableSnapshot.CreateClusterMembershipSnapshot();
            this.updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                initialValue: this.snapshot,
                updateValidator: (previous, proposed) => proposed.IsSuccessorTo(previous),
                onPublished: update => Interlocked.Exchange(ref this.snapshot, update));
            this.membershipManager = membershipManager;
            this.log = log;
            this.fatalErrorHandler = fatalErrorHandler;
        }

        public ClusterMembershipSnapshot CurrentSnapshot
        {
            get
            {
                var tableSnapshot = this.membershipManager.CurrentSnapshot;
                if (ReferenceEquals(Volatile.Read(ref this.tableSnapshot), tableSnapshot))
                {
                    return Volatile.Read(ref this.snapshot);
                }

                var currentSnapshot = Volatile.Read(ref this.snapshot);
                if (currentSnapshot.Version > tableSnapshot.Version)
                {
                    return currentSnapshot;
                }

                this.TryPublish(tableSnapshot);
                return Volatile.Read(ref this.snapshot);
            }
        }

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => this.updates;

        public ValueTask Refresh(MembershipVersion targetVersion) => Refresh(targetVersion, CancellationToken.None);
        public ValueTask Refresh(MembershipVersion targetVersion, CancellationToken cancellationToken)
        {
            if (targetVersion != default && targetVersion != MembershipVersion.MinValue && this.snapshot.Version >= targetVersion)
                return default;

            return RefreshAsync(targetVersion, cancellationToken);

            async ValueTask RefreshAsync(MembershipVersion v, CancellationToken cancellationToken)
            {
                var didRefresh = false;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!didRefresh || this.membershipManager.CurrentSnapshot.Version < v)
                    {
                        await this.membershipManager.Refresh(null, cancellationToken);
                        didRefresh = true;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
                } while (this.snapshot.Version < v || this.snapshot.Version < this.membershipManager.CurrentSnapshot.Version);
            }
        }

        public async Task<bool> TryKill(SiloAddress siloAddress) => await this.membershipManager.TryKillSilo(siloAddress, CancellationToken.None);

        private async Task ProcessMembershipUpdates(CancellationToken ct)
        {
            try
            {
                LogDebugStartingToProcessMembershipUpdates(log);
                await foreach (var tableSnapshot in this.membershipManager.MembershipUpdates.WithCancellation(ct))
                {
                    this.TryPublish(tableSnapshot);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Ignore and continue shutting down.
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                LogErrorProcessingMembershipUpdates(log, exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                LogDebugStoppingMembershipUpdateProcessor(log);
            }
        }

        internal bool TryPublish(ClusterMembershipSnapshot proposed)
            => this.updates.TryPublish(
                static (previous, proposed) => proposed.MergeMetadata(previous),
                proposed);

        private bool TryPublish(MembershipTableSnapshot proposed)
        {
            var result = this.TryPublish(proposed.CreateClusterMembershipSnapshot());
            Volatile.Write(ref this.tableSnapshot, proposed);
            return result;
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>(1);
            var cancellation = new CancellationTokenSource();
            Task OnRuntimeInitializeStart(CancellationToken _)
            {
                tasks.Add(Task.Run(() => this.ProcessMembershipUpdates(cancellation.Token), CancellationToken.None));
                return Task.CompletedTask;
            }

            async Task OnRuntimeInitializeStop(CancellationToken ct)
            {
                cancellation.Cancel(throwOnFirstException: false);
                var shutdownGracePeriod = Task.WhenAll(Task.Delay(ClusterMembershipOptions.ClusteringShutdownGracePeriod, CancellationToken.None), ct.WhenCancelled());
                await Task.WhenAny(shutdownGracePeriod, Task.WhenAll(tasks));
            }

            lifecycle.Subscribe(
                nameof(ClusterMembershipService),
                ServiceLifecycleStage.RuntimeInitialize,
                OnRuntimeInitializeStart,
                OnRuntimeInitializeStop);
        }

        void IDisposable.Dispose()
        {
            this.updates.Dispose();
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Starting to process membership updates"
        )]
        private static partial void LogDebugStartingToProcessMembershipUpdates(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error processing membership updates"
        )]
        private static partial void LogErrorProcessingMembershipUpdates(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Stopping membership update processor"
        )]
        private static partial void LogDebugStoppingMembershipUpdateProcessor(ILogger logger);
    }
}
