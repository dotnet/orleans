using System;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    public class DIGrainWithInjectedServices : Grain, IDIGrainWithInjectedServices
    {
        private readonly IInjectedService injectedService;
        private readonly IGrainFactory injectedGrainFactory;
        private readonly long grainFactoryId;
        public static readonly ObjectIDGenerator ObjectIdGenerator = new ObjectIDGenerator();

        public DIGrainWithInjectedServices(IInjectedService injectedService, IGrainFactory injectedGrainFactory)
        {
            this.injectedService = injectedService;
            this.injectedGrainFactory = injectedGrainFactory;
            bool set;
            // get the object Id for injected GrainFactory, 
            // object Id will be the same if the underlying object is the same,
            // this is one way to prove that this GrainFactory is injected from DI
            this.grainFactoryId = ObjectIdGenerator.GetId(this.injectedGrainFactory, out set);
        }

        public Task<long> GetTicksFromService()
        {
            return injectedService.GetTicks();
        }

        public Task<string> GetStringValue()
        {
            return Task.FromResult(this.injectedService.GetInstanceValue());
        }

        public Task<long> GetGrainFactoryId()
        {
            return Task.FromResult(this.grainFactoryId);
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
