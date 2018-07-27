using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class TestGrain : Grain, ITestGrain
    {
        private string label;
        private Logger logger;
        private IDisposable timer;

        public override Task OnActivateAsync()
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            logger = this.GetLogger("TestGrain " + Data.Address);
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

    internal class GuidTestGrain : Grain, IGuidTestGrain
    {
        private string label;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            //if (this.GetPrimaryKeyLong() == -2)
            //    throw new ArgumentException("Primary key cannot be -2 for this test case");

            label = this.GetPrimaryKey().ToString();
            logger = this.GetLogger("GuidTestGrain " + Data.Address);
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

    public class OneWayGrain : Grain, IOneWayGrain
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

        public async Task<bool> NotifyOtherGrain(IOneWayGrain otherGrain, ISimpleGrainObserver observer)
        {
            var task = otherGrain.Notify(observer);
            var completedSynchronously = task.Status == TaskStatus.RanToCompletion;
            await task;
            return completedSynchronously;
        }

        public Task<int> GetCount() => Task.FromResult(this.count);

        public Task ThrowsOneWay()
        {
            throw new Exception("GET OUT!");
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

        public Task<int> GetCount() => Task.FromResult(this.count);

        public Task Throws()
        {
            throw new Exception("GET OUT!");
        }
    }
}
