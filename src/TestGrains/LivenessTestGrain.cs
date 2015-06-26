using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTestGrains
{
    internal class LivenessTestGrain : Grain, ILivenessTestGrain
    {
        private string label;
        private Logger logger;
        private IDisposable timer;
        private Guid uniqueId;

        public override Task OnActivateAsync()
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            uniqueId = Guid.NewGuid();
            logger = GetLogger("LivenessTestGrain " + uniqueId);
            label = this.GetPrimaryKeyLong().ToString();
            logger.Info("OnActivateAsync");

            return base.OnActivateAsync();
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("!!! OnDeactivateAsync");
            return base.OnDeactivateAsync();
        }

        #region Implementation of ILivenessTestGrain

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
            return TaskDone.Done;
        }

        public Task StartTimer()
        {
            logger.Info("StartTimer.");
            timer = base.RegisterTimer(TimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            
            return TaskDone.Done;
        }

        private Task TimerTick(object data)
        {
            logger.Info("TimerTick.");
            return TaskDone.Done;
        }

        public async Task<Tuple<string, string>> TestRequestContext()
        {
            string bar1 = null;
            RequestContext.Set("jarjar", "binks");

            Task task = Task.Factory.StartNew(() =>
            {
                bar1 = (string)RequestContext.Get("jarjar");
                logger.Info("bar = {0}.", bar1);
            });

            string bar2 = null;
            Task ac = Task.Factory.StartNew(() =>
            {
                bar2 = (string)RequestContext.Get("jarjar");
                logger.Info("bar = {0}.", bar2);
            });

            await Task.WhenAll(task, ac);
            return new Tuple<string, string>(bar1, bar2);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<string> GetUniqueId()
        {
            return Task.FromResult(uniqueId.ToString());
        }

        public Task<ILivenessTestGrain> GetGrainReference()
        {
            return Task.FromResult(this.AsReference<ILivenessTestGrain>());
        }

        public Task<IGrain[]> GetMultipleGrainInterfaces_Array()
        {
            IGrain[] grains = new IGrain[5];
            for (int i = 0; i < grains.Length; i++)
            {
                grains[i] = GrainFactory.GetGrain<ILivenessTestGrain>(i);
            }
            return Task.FromResult(grains);
        }

        public Task<List<IGrain>> GetMultipleGrainInterfaces_List()
        {
            IGrain[] grains = new IGrain[5];
            for (int i = 0; i < grains.Length; i++)
            {
                grains[i] = GrainFactory.GetGrain<ILivenessTestGrain>(i);
            }
            return Task.FromResult(grains.ToList());
        }

        #endregion
    }
}
