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
    internal class ClusterMembershipService : IClusterMembershipService, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly AsyncEnumerable<ClusterMembershipSnapshot> updates;
        private readonly MembershipTableManager membershipTableManager;
        private readonly ILogger<ClusterMembershipService> log;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private ClusterMembershipSnapshot snapshot;

        public ClusterMembershipService(
            MembershipTableManager membershipTableManager,
            ILogger<ClusterMembershipService> log,
            IFatalErrorHandler fatalErrorHandler)
        {
            snapshot = membershipTableManager.MembershipTableSnapshot.CreateClusterMembershipSnapshot();
            updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                (previous, proposed) => proposed.Version == MembershipVersion.MinValue || proposed.Version > previous.Version,
                snapshot)
            {
                OnPublished = update => Interlocked.Exchange(ref snapshot, update)
            };
            this.membershipTableManager = membershipTableManager;
            this.log = log;
            this.fatalErrorHandler = fatalErrorHandler;
        }

        public ClusterMembershipSnapshot CurrentSnapshot
        {
            get
            {
                var tableSnapshot = membershipTableManager.MembershipTableSnapshot;
                if (snapshot.Version == tableSnapshot.Version)
                {
                    return snapshot;
                }

                updates.TryPublish(tableSnapshot.CreateClusterMembershipSnapshot());
                return snapshot;
            }
        }

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => updates;

        public ValueTask Refresh(MembershipVersion targetVersion)
        {
            if (targetVersion != default && targetVersion != MembershipVersion.MinValue && snapshot.Version >= targetVersion)
                return default;

            return RefreshAsync(targetVersion);

            async ValueTask RefreshAsync(MembershipVersion v)
            {
                var didRefresh = false;
                do
                {
                    if (!didRefresh || membershipTableManager.MembershipTableSnapshot.Version < v)
                    {
                        await membershipTableManager.Refresh();
                        didRefresh = true;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                } while (snapshot.Version < v || snapshot.Version < membershipTableManager.MembershipTableSnapshot.Version);
            }
        }

        public async Task<bool> TryKill(SiloAddress siloAddress) => await membershipTableManager.TryKill(siloAddress);

        private async Task ProcessMembershipUpdates(CancellationToken ct)
        {
            try
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Starting to process membership updates");
                await foreach (var tableSnapshot in membershipTableManager.MembershipTableUpdates.WithCancellation(ct))
                {
                    updates.TryPublish(tableSnapshot.CreateClusterMembershipSnapshot());
                }
            }
            catch (Exception exception) when (fatalErrorHandler.IsUnexpected(exception))
            {
                log.LogError(exception, "Error processing membership updates");
                fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (log.IsEnabled(LogLevel.Debug)) log.LogDebug("Stopping membership update processor");
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>(1);
            var cancellation = new CancellationTokenSource();
            Task OnRuntimeInitializeStart(CancellationToken _)
            {
                tasks.Add(Task.Run(() => ProcessMembershipUpdates(cancellation.Token)));
                return Task.CompletedTask;
            }

            async Task OnRuntimeInitializeStop(CancellationToken ct)
            {
                cancellation.Cancel(throwOnFirstException: false);
                var shutdownGracePeriod = Task.WhenAll(Task.Delay(ClusterMembershipOptions.ClusteringShutdownGracePeriod), ct.WhenCancelled());
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
            updates.Dispose();
        }
    }
}
