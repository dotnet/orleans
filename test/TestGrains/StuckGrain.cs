﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    public class StuckGrain : Grain, IStuckGrain
    {
        private static Dictionary<Guid, TaskCompletionSource<bool>> tcss = new Dictionary<Guid, TaskCompletionSource<bool>>();
        private static HashSet<Guid> grains = new HashSet<Guid>();

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

        public override Task OnActivateAsync()
        {
            lock (grains)
            {
                grains.Add(this.GetPrimaryKey());
            }
            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            grains.Remove(this.GetPrimaryKey());
            return base.OnDeactivateAsync();
        }
    }


    public class StuckCleanupGrain : Grain, IStuckCleanGrain
    {
        public Task Release(Guid key)
        {
            StuckGrain.Release(key);
            return TaskDone.Done;
        }

        public Task<bool> IsActivated(Guid key)
        {
            return Task.FromResult(StuckGrain.IsActivated(key));
        }
    }
}
