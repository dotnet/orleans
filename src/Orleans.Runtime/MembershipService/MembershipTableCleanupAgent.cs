using System;
using System.Collections.Generic;
using Orleans.Configuration;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Responsible for cleaning up dead membership table entries.
    /// </summary>
    internal class MembershipTableCleanupAgent : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ClusterMembershipOptions clusterMembershipOptions;
        private readonly IMembershipTable membershipTableProvider;
        private readonly ILogger<MembershipTableCleanupAgent> log;

        public MembershipTableCleanupAgent(
            IOptions<ClusterMembershipOptions> clusterMembershipOptions,
            IMembershipTable membershipTableProvider,
            ILogger<MembershipTableCleanupAgent> log)
        {
            this.clusterMembershipOptions = clusterMembershipOptions.Value;
            this.membershipTableProvider = membershipTableProvider;
            this.log = log;
        }

        private async Task CleanupDefunctSios()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();

            if (this.clusterMembershipOptions.DefunctSiloCleanupPeriod == default)
            {
                if (this.log.IsEnabled(LogLevel.Debug))
                {
                    this.log.LogDebug($"Membership table cleanup is disabled due to {nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.DefunctSiloCleanupPeriod)} not being specified");
                }

                return;
            }

            var dateLimit = DateTime.UtcNow - this.clusterMembershipOptions.DefunctSiloExpiration;
            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting membership table cleanup agent");
            try
            {
                var period = this.clusterMembershipOptions.DefunctSiloCleanupPeriod.Value;
                while (!this.cancellation.IsCancellationRequested)
                {
                    var task = await Task.WhenAny(Task.Delay(period), cancellationTask);
                    if (ReferenceEquals(task, cancellationTask)) break;

                    try
                    {
                        await this.membershipTableProvider.CleanupDefunctSiloEntries(dateLimit);
                    }
                    catch (Exception exception) when (exception is NotImplementedException || exception is MissingMethodException)
                    {
                        this.log.Error(
                            ErrorCode.MembershipCleanDeadEntriesFailure,
                            "DeleteDeadMembershipTableEntries operation is not supported by the current implementation of IMembershipTable. Disabling the timer now.");
                        return;
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError((int)ErrorCode.MembershipCleanDeadEntriesFailure, "Failed to clean up defunct membership table entries: {Exception}", exception);
                    }
                }
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopped membership table cleanup agent");
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>();
            lifecycle.Subscribe(nameof(MembershipTableCleanupAgent), ServiceLifecycleStage.RuntimeGrainServices, OnStart, OnStop);

            Task OnStart(CancellationToken ct)
            {
                tasks.Add(this.CleanupDefunctSios());
                return Task.CompletedTask;
            }

            async Task OnStop(CancellationToken ct)
            {
                this.cancellation.Cancel(throwOnFirstException: false);
                await Task.WhenAny(ct.WhenCancelled(), Task.WhenAll(tasks));
            }
        }
    }
}
