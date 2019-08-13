using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
            this.snapshot = membershipTableManager.MembershipTableSnapshot.CreateClusterMembershipSnapshot();
            this.updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                (previous, proposed) => proposed.Version == MembershipVersion.MinValue || proposed.Version > previous.Version,
                this.snapshot)
            {
                OnPublished = update => Interlocked.Exchange(ref this.snapshot, update)
            };
            this.membershipTableManager = membershipTableManager;
            this.log = log;
            this.fatalErrorHandler = fatalErrorHandler;
        }

        public ClusterMembershipSnapshot CurrentSnapshot => this.snapshot;

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => this.updates;

        public ValueTask Refresh(MembershipVersion targetVersion)
        {
            if (targetVersion != default && targetVersion != MembershipVersion.MinValue && this.snapshot.Version >= targetVersion)
                return default;

            return RefreshAsync(targetVersion);

            async ValueTask RefreshAsync(MembershipVersion v)
            {
                var didRefresh = false;
                do
                {
                    if (!didRefresh || this.membershipTableManager.MembershipTableSnapshot.Version < v)
                    {
                        await this.membershipTableManager.Refresh();
                        didRefresh = true;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                } while (this.snapshot.Version < v || this.snapshot.Version < this.membershipTableManager.MembershipTableSnapshot.Version);
            }
        }

        private async Task ProcessMembershipUpdates(CancellationToken ct)
        {
            IAsyncEnumerator<MembershipTableSnapshot> enumerator = default;
            try
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
                enumerator = this.membershipTableManager.MembershipTableUpdates.GetAsyncEnumerator(ct);
                while (await enumerator.MoveNextAsync())
                {
                    this.updates.TryPublish(enumerator.Current.CreateClusterMembershipSnapshot());
                }
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                this.log.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (enumerator is object) await enumerator.DisposeAsync();
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping membership update processor");
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>(1);
            var cancellation = new CancellationTokenSource();
            Task OnRuntimeInitializeStart(CancellationToken _)
            {
                tasks.Add(Task.Run(() => this.ProcessMembershipUpdates(cancellation.Token)));
                return Task.CompletedTask;
            }

            async Task OnRuntimeInitializeStop(CancellationToken ungracefulCancellation)
            {
                cancellation.Cancel(throwOnFirstException: false);
                await Task.WhenAny(ungracefulCancellation.WhenCancelled(), Task.WhenAll(tasks));
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
    }
}
