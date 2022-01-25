using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    public class StuckGrain : Grain, IStuckGrain
    {
        private static ConcurrentDictionary<GrainId, bool> ActivationCalls = new();
        private static Dictionary<Guid, TaskCompletionSource<bool>> tcss = new Dictionary<Guid, TaskCompletionSource<bool>>();
        private static Dictionary<Guid, int> counters = new Dictionary<Guid, int>();
        private static HashSet<Guid> grains = new HashSet<Guid>();
        private readonly ILogger<StuckGrain> _log;
        private bool isDeactivatingBlocking = false;

        public StuckGrain(ILogger<StuckGrain> log)
        {
            _log = log;
        }

        public static bool Release(Guid key)
        {
            lock (tcss)
            {
                if (!tcss.ContainsKey(key))
                    return false;

                tcss[key].TrySetResult(true);
                tcss.Remove(key);
                return true;
            }
        }

        public static bool IsActivated(Guid key)
        {
            return grains.Contains(key);
        }

        public Task RunForever()
        {
            var key = this.GetPrimaryKey();

            lock (tcss)
            {
                if(tcss.ContainsKey(key))
                    throw new InvalidOperationException("Duplicate call for the same grain ID.");

                var tcs = new TaskCompletionSource<bool>();
                tcss[key] = tcs;
                return tcs.Task;
            }
        }

        public Task NonBlockingCall()
        {
            counters[this.GetPrimaryKey()] = counters[this.GetPrimaryKey()] + 1;
            return Task.CompletedTask;
        }

        public Task<int> GetNonBlockingCallCounter()
        {
            return Task.FromResult(counters[this.GetPrimaryKey()]);
        }

        public Task<bool> DidActivationTryToStart(GrainId id)
        {
            return Task.FromResult(ActivationCalls.TryGetValue(id, out _));
        }

        public Task BlockingDeactivation()
        {
            isDeactivatingBlocking = true;
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ActivationCalls[this.GetGrainId()] = true;

            _log.LogInformation("Activating");
            var key = this.GetPrimaryKey();
            lock (grains)
            {
                grains.Add(key);
            }

            lock (counters)
            {
                counters[key] = 0;
            }

            if (RequestContext.Get("block_activation_seconds") is int blockActivationSeconds && blockActivationSeconds > 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(blockActivationSeconds), cancellationToken);
                }
                catch (Exception exception)
                {
                    _log.LogInformation(exception, "Error while waiting");
                }
            }

            await base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _log.LogInformation(reason.Exception, "Deactivating ReasonCode: {ReasonCode} Description: {ReasonText}", reason.ReasonCode, reason.Description);

            if (isDeactivatingBlocking) return RunForever();

            var key = this.GetPrimaryKey();
            lock (grains)
            {
                grains.Remove(key);
            }
            lock (tcss)
            {
                tcss.Remove(key);
            }
            return base.OnDeactivateAsync(reason, cancellationToken);
        }
    }


    public class StuckCleanupGrain : Grain, IStuckCleanGrain
    {
        public Task Release(Guid key)
        {
            StuckGrain.Release(key);
            return Task.CompletedTask;
        }

        public Task<bool> IsActivated(Guid key)
        {
            return Task.FromResult(StuckGrain.IsActivated(key));
        }
    }
}
