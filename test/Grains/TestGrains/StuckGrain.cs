using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    public class StuckGrain : Grain, IStuckGrain
    {
        private static Dictionary<Guid, TaskCompletionSource<bool>> tcss = new Dictionary<Guid, TaskCompletionSource<bool>>();
        private static Dictionary<Guid, int> counters = new Dictionary<Guid, int>();
        private static HashSet<Guid> grains = new HashSet<Guid>();
        private bool isDeactivatingBlocking = false;

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

        public Task BlockingDeactivation()
        {
            isDeactivatingBlocking = true;
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public override Task OnActivateAsync()
        {
            var key = this.GetPrimaryKey();
            lock (grains)
            {
                grains.Add(key);
            }
            lock (counters)
            {
                counters[key] = 0;
            }
            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
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
            return base.OnDeactivateAsync();
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
