using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    public class SimpleDIGrain : Grain, ISimpleDIGrain
    {
        private readonly IInjectedService injectedService;

        public SimpleDIGrain(IInjectedService injectedService)
        {
            this.injectedService = injectedService;
        }

        public Task<long> GetTicksFromService()
        {
            return injectedService.GetTicks();
        }

        public Task<string> GetStringValue()
        {
            return Task.FromResult(this.injectedService.GetInstanceValue());
        }
    }

    public class ExplicitlyRegisteredSimpleDIGrain : Grain, ISimpleDIGrain
    {
        private readonly IInjectedService injectedService;
        private string someValueThatIsNotRegistered;

        public ExplicitlyRegisteredSimpleDIGrain(IInjectedService injectedService, string someValueThatIsNotRegistered)
        {
            this.injectedService = injectedService;
            this.someValueThatIsNotRegistered = someValueThatIsNotRegistered;
        }

        public Task<long> GetTicksFromService()
        {
            return injectedService.GetTicks();
        }

        public Task<string> GetStringValue()
        {
           return Task.FromResult(this.someValueThatIsNotRegistered);
        }
    }

    public interface IInjectedService
    {
        Task<long> GetTicks();
        string GetInstanceValue();
    }

    public class InjectedService : IInjectedService
    {
        private readonly string instanceValue = Guid.NewGuid().ToString();

        public Task<long> GetTicks()
        {
            return Task.FromResult(DateTime.UtcNow.Ticks);
        }

        public string GetInstanceValue()
        {
            return this.instanceValue;
        }
    }
}
