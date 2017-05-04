using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
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

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
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

        #endregion
    }
}
