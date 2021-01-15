using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class TestGrain : Grain, ITestGrain
    {
        private string label;
        private ILogger logger;
        private IDisposable timer;

        public TestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync()
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            label = this.GetPrimaryKeyLong().ToString();
            logger.Info("OnActivateAsync");

            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("!!! OnDeactivateAsync");
            return base.OnDeactivateAsync();
        }

        public Task<long> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKeyLong());
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public async Task DoLongAction(TimeSpan timespan, string str)
        {
            logger.Info("DoLongAction {0} received", str);
            await Task.Delay(timespan);
        }

        public Task SetLabel(string label)
        {
            this.label = label;
            logger.Info("SetLabel {0} received", label);
            return Task.CompletedTask;
        }

        public Task StartTimer()
        {
            logger.Info("StartTimer.");
            timer = base.RegisterTimer(TimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            
            return Task.CompletedTask;
        }

        private Task TimerTick(object data)
        {
            logger.Info("TimerTick.");
            return Task.CompletedTask;
        }

        public async Task<Tuple<string, string>> TestRequestContext()
        {
            string bar1 = null;
            RequestContext.Set("jarjar", "binks");

            var task = Task.Factory.StartNew(() =>
            {
                bar1 = (string) RequestContext.Get("jarjar");
                logger.Info("bar = {0}.", bar1);
            });

            string bar2 = null;
            var ac = Task.Factory.StartNew(() =>
            {
                bar2 = (string) RequestContext.Get("jarjar");
                logger.Info("bar = {0}.", bar2);
            });

            await Task.WhenAll(task, ac);
            return new Tuple<string, string>(bar1, bar2);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }

        public Task<ITestGrain> GetGrainReference()
        {
            return Task.FromResult(this.AsReference<ITestGrain>());
        }

        public Task<IGrain[]> GetMultipleGrainInterfaces_Array()
        {
            var grains = new IGrain[5];
            for (var i = 0; i < grains.Length; i++)
            {
                grains[i] = GrainFactory.GetGrain<ITestGrain>(i);
            }
            return Task.FromResult(grains);
        }

        public Task<List<IGrain>> GetMultipleGrainInterfaces_List()
        {
            var grains = new IGrain[5];
            for (var i = 0; i < grains.Length; i++)
            {
                grains[i] = GrainFactory.GetGrain<ITestGrain>(i);
            }
            return Task.FromResult(grains.ToList());
        }
    }

    public class TestGrainLongActivateAsync : Grain, ITestGrainLongOnActivateAsync
    {
        public TestGrainLongActivateAsync()
        {
        }

        public override async Task OnActivateAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            await base.OnActivateAsync();
        }

        public Task<long> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKeyLong());
        }
    }

    internal class GuidTestGrain : Grain, IGuidTestGrain
    {
        private string label;
        private ILogger logger;

        public GuidTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync()
        {
            //if (this.GetPrimaryKeyLong() == -2)
            //    throw new ArgumentException("Primary key cannot be -2 for this test case");

            label = this.GetPrimaryKey().ToString();
            logger.Info("OnActivateAsync");

            return Task.CompletedTask;
        }

        public Task<Guid> GetKey()
        {
            return Task.FromResult(this.GetPrimaryKey());
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public Task SetLabel(string label)
        {
            this.label = label;
            return Task.CompletedTask;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<string> GetActivationId()
        {
            return Task.FromResult(Data.ActivationId.ToString());
        }
    }

    internal class OneWayGrain : Grain, IOneWayGrain, ISimpleGrainObserver
    {
        private int count;
        private TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
        private IOneWayGrain other;
        private Catalog catalog;

        public OneWayGrain(Catalog catalog)
        {
            this.catalog = catalog;
        }

        private ILocalGrainDirectory LocalGrainDirectory => this.ServiceProvider.GetRequiredService<ILocalGrainDirectory>();
        private ILocalSiloDetails LocalSiloDetails => this.ServiceProvider.GetRequiredService<ILocalSiloDetails>();

        public Task Notify()
        {
            this.count++;
            return Task.CompletedTask;
        }


        public Task Notify(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return Task.CompletedTask;
        }

        public ValueTask NotifyValueTask(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return default;
        }

        public async Task<bool> NotifyOtherGrain(IOneWayGrain otherGrain, ISimpleGrainObserver observer)
        {
            var task = otherGrain.Notify(observer);
            var completedSynchronously = task.Status == TaskStatus.RanToCompletion;
            await task;
            return completedSynchronously;
        }

        public async Task<bool> NotifyOtherGrainValueTask(IOneWayGrain otherGrain, ISimpleGrainObserver observer)
        {
            var task = otherGrain.NotifyValueTask(observer);
            var completedSynchronously = task.IsCompleted;
            await task;
            return completedSynchronously;
        }

        public async Task<IOneWayGrain> GetOtherGrain()
        {
            return this.other ?? (this.other = await GetGrainOnOtherSilo());

            async Task<IOneWayGrain> GetGrainOnOtherSilo()
            {
                while (true)
                {
                    var candidate = this.GrainFactory.GetGrain<IOneWayGrain>(Guid.NewGuid());
                    var directorySilo = await candidate.GetPrimaryForGrain();
                    var thisSilo = await this.GetSiloAddress();
                    var candidateSilo = await candidate.GetSiloAddress();
                    if (!directorySilo.Equals(candidateSilo)
                        && !directorySilo.Equals(thisSilo)
                        && !candidateSilo.Equals(thisSilo))
                    {
                        return candidate;
                    }
                }
            }
        }

        public Task<string> GetActivationAddress(IGrain grain)
        {
            var grainId = ((GrainReference)grain).GrainId;
            if (this.catalog.FastLookup(grainId, out var addresses))
            {
                return Task.FromResult(addresses.Single().ToString());
            }

            return Task.FromResult<string>(null);
        }

        public Task NotifyOtherGrain() => this.other.Notify(this.AsReference<ISimpleGrainObserver>());

        public Task<int> GetCount() => Task.FromResult(this.count);

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task ThrowsOneWay()
        {
            throw new Exception("GET OUT!");
        }

        public ValueTask ThrowsOneWayValueTask()
        {
            throw new Exception("GET OUT (ValueTask)!");
        }

        public Task<SiloAddress> GetSiloAddress()
        {
            return Task.FromResult(this.LocalSiloDetails.SiloAddress);
        }

        public Task<SiloAddress> GetPrimaryForGrain()
        {
            var grainId = (GrainId)this.GrainId;
            var primaryForGrain = this.LocalGrainDirectory.GetPrimaryForGrain(grainId);
            return Task.FromResult(primaryForGrain);
        }

        public void StateChanged(int a, int b)
        {
            this.tcs.TrySetResult(0);
        }
    }

    public class CanBeOneWayGrain : Grain, ICanBeOneWayGrain
    {
        private int count;

        public Task Notify()
        {
            this.count++;
            return Task.CompletedTask;
        }

        public Task Notify(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return Task.CompletedTask;
        }

        public ValueTask NotifyValueTask(ISimpleGrainObserver observer)
        {
            this.count++;
            observer.StateChanged(this.count - 1, this.count);
            return default;
        }

        public Task<int> GetCount() => Task.FromResult(this.count);

        public Task Throws()
        {
            throw new Exception("GET OUT!");
        }

        public ValueTask ThrowsValueTask()
        {
            throw new Exception("GET OUT!");
        }
    }
}
