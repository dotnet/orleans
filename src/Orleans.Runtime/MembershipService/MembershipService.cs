using System;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipService : ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        private readonly MembershipTableManager tableManager;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ILocalSiloDetails localSilo;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private readonly ILogger<MembershipService> log;
        private readonly ChangeFeedSource<ClusterMembershipSnapshot> updates;
        private Task processUpdatesTask;

        public MembershipService(
            MembershipTableManager tableManager,
            ILocalSiloDetails localSilo,
            IFatalErrorHandler fatalErrorHandler,
            ILogger<MembershipService> log)
        {
            this.tableManager = tableManager;
            this.localSilo = localSilo;
            this.fatalErrorHandler = fatalErrorHandler;
            this.log = log;
            this.CurrentMembership = this.Create(tableManager.MembershipTableSnapshot);
            this.updates = new ChangeFeedSource<ClusterMembershipSnapshot>(
                (previous, proposed) => proposed.Version > previous.Version,
                this.CurrentMembership);
        }

        public ClusterMembershipSnapshot CurrentMembership { get; private set; }

        public ChangeFeedEntry<ClusterMembershipSnapshot> MembershipUpdates => this.updates.Current;

        private async Task ProcessUpdates()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();
            var current = this.tableManager.MembershipTableUpdates;

            this.log.LogInformation($"Starting {nameof(MembershipService)}");
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    var next = current.NextAsync();

                    // Handle graceful termination.
                    var task = await Task.WhenAny(next, cancellationTask);
                    if (ReferenceEquals(task, cancellationTask)) break;

                    current = next.GetAwaiter().GetResult();

                    if (!current.HasValue)
                    {
                        this.log.LogWarning("Received a membership update with no data");
                        continue;
                    }

                    var snapshot = this.Create(current.Value);
                    this.CurrentMembership = snapshot;
                    this.updates.Publish(snapshot);
                }
            }
            catch (Exception exception)
            {
                // Any exception here is fatal
                this.log.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessUpdates), exception);
            }
            finally
            {
                this.log.LogInformation($"Shutting down {nameof(MembershipService)}");
            }
        }

        private ClusterMembershipSnapshot Create(MembershipTableSnapshot table) => ClusterMembershipSnapshot.Create(table);

        public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe(ServiceLifecycleStage.RuntimeInitialize, this);

        public Task OnStart(CancellationToken ct)
        {
            IntValueStatistic.FindOrCreate(StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER_SIZE, () => this.CurrentMembership.Members.Count);
            StringValueStatistic.FindOrCreate(
                StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER,
                () =>
                {
                    var list = new List<string>();
                    foreach (var item in this.CurrentMembership.Members)
                    {
                        var entry = item.Value;
                        if (entry.Status != SiloStatus.Active) continue;
                        list.Add(entry.SiloAddress.ToLongString());
                    }

                    list.Sort();
                    return Utils.EnumerableToString(list);
                });

            this.processUpdatesTask = this.ProcessUpdates();
            return Task.CompletedTask;
        }

        public Task OnStop(CancellationToken ct)
        {
            this.cancellation.Cancel(throwOnFirstException: false);
            return this.processUpdatesTask ?? Task.CompletedTask;
        }
    }
}
