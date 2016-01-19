using System;
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
    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class SimpleDIGrain : Grain, ISimpleDIGrain
    {
        private readonly IInjectedService injectedService;

        protected Logger logger;

        public SimpleDIGrain(IInjectedService injectedService)
        {
            this.injectedService = injectedService;
        }

        public override Task OnActivateAsync()
        {
            logger = GetLogger(String.Format("{0}-{1}-{2}", typeof(SimpleDIGrain).Name, base.IdentityString, base.RuntimeIdentity));
            logger.Info("Activate.");
            return TaskDone.Done;
        }

        public Task<long> GetTicksFromService()
        {
            return injectedService.GetTicks();
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync.");
            return TaskDone.Done;
        }
    }

    public interface IInjectedService
    {
        Task<long> GetTicks();
    }

    public class InjectedService : IInjectedService
    {
        public Task<long> GetTicks()
        {
            return Task.FromResult(DateTime.UtcNow.Ticks);
        }
    }
}
